using Hangfire;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Filters;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;

namespace Lingarr.Server.Jobs;

/// <summary>
/// One-shot maintenance, launched on demand from the Schedule page.
/// Normalizes translated subtitle file names so the LANGUAGE is always the
/// final filename segment: Jellyfin/Emby identify an external subtitle by the
/// last token, so files written by older versions as "Movie.th.hi.srt" showed
/// up as Hindi (or were ignored) while Lingarr's own trailing-tag parser read
/// them back as th+hi — the Thai track looked missing although the file
/// existed. Renames to "Movie.hi.th.srt" and updates the paths recorded on
/// translation requests so nothing gets re-translated. Also covers junk
/// suffixes such as VLC's ".synced" (Movie.th.synced.srt → Movie.synced.th.srt).
/// </summary>
public class SubtitleNamingRepairJob
{
    /// <summary>Caption tags recognized by Lingarr's filename parser.</summary>
    private static readonly HashSet<string> Captions = new(StringComparer.OrdinalIgnoreCase)
        { "sdh", "cc", "forced", "hi" };

    private readonly LingarrDbContext _dbContext;
    private readonly ILogger<SubtitleNamingRepairJob> _logger;
    private readonly IScheduleService _scheduleService;
    private readonly LanguageCodeService _languageCodeService;

    public SubtitleNamingRepairJob(
        LingarrDbContext dbContext,
        IScheduleService scheduleService,
        LanguageCodeService languageCodeService,
        ILogger<SubtitleNamingRepairJob> logger)
    {
        _dbContext = dbContext;
        _scheduleService = scheduleService;
        _languageCodeService = languageCodeService;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [Queue("system")]
    public async Task Execute()
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

        var directories = await _dbContext.Movies
            .Where(m => m.Path != null)
            .Select(m => m.Path!)
            .Concat(_dbContext.Episodes
                .Where(e => e.Path != null)
                .Select(e => e.Path!))
            .Distinct()
            .ToListAsync();

        var scanned = 0;
        var renamed = 0;
        var skipped = 0;
        var failures = 0;
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            string[] files;
            try
            {
                // AllDirectories includes Synology's @eaDir metadata duplicates,
                // so every copy of a subtitle follows the standard naming.
                files = Directory.GetFiles(directory, "*.srt", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subtitle naming repair: cannot list {Directory}.", directory);
                continue;
            }

            foreach (var file in files)
            {
                scanned++;
                var fileName = Path.GetFileName(file);
                var normalized = NormalizeFileName(fileName, _languageCodeService.Validate);
                if (normalized == null)
                {
                    continue;
                }

                var newPath = Path.Combine(Path.GetDirectoryName(file)!, normalized);
                if (File.Exists(newPath))
                {
                    skipped++;
                    _logger.LogWarning(
                        "Subtitle naming repair: target {Target} already exists, keeping {File} untouched.",
                        newPath, file);
                    continue;
                }

                try
                {
                    File.Move(file, newPath);
                }
                catch (Exception ex)
                {
                    failures++;
                    _logger.LogWarning(ex, "Subtitle naming repair: rename failed for {File}.", file);
                    continue;
                }

                renamed++;
                renames[file] = newPath;
                _logger.LogInformation("Subtitle naming repair: {Old} -> {New}", file, newPath);
            }
        }

        var updated = await UpdateRecordedPaths(renames);

        _logger.LogInformation(
            "Subtitle naming repair complete: {Scanned} scanned, {Renamed} renamed, {DbUpdated} database paths updated, {Skipped} skipped (target exists), {Failures} failures.",
            scanned, renamed, updated, skipped, failures);
        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
    }

    /// <summary>Re-points translation requests at the renamed files.</summary>
    private async Task<int> UpdateRecordedPaths(Dictionary<string, string> renames)
    {
        if (renames.Count == 0)
        {
            return 0;
        }

        var requests = await _dbContext.TranslationRequests
            .Where(r => r.TranslatedSubtitle != null || r.SubtitleToTranslate != null)
            .ToListAsync();

        var updated = 0;
        foreach (var request in requests)
        {
            var changed = false;
            if (request.TranslatedSubtitle != null
                && renames.TryGetValue(request.TranslatedSubtitle, out var translated))
            {
                request.TranslatedSubtitle = translated;
                changed = true;
            }

            if (request.SubtitleToTranslate != null
                && renames.TryGetValue(request.SubtitleToTranslate, out var source))
            {
                request.SubtitleToTranslate = source;
                changed = true;
            }

            if (changed)
            {
                updated++;
            }
        }

        if (updated > 0)
        {
            await _dbContext.SaveChangesAsync();
        }

        return updated;
    }

    /// <summary>
    /// Pure normalization rule, mirroring Lingarr's trailing-tag parser
    /// (basename[.lang][.caption], max two meaningful trailing tokens, plus one
    /// junk suffix such as ".synced"). Returns the normalized file name, or null
    /// when the name already ends with the language tag (or carries none).
    /// </summary>
    /// <param name="fileName">File name with extension, e.g. "Movie.th.hi.srt".</param>
    /// <param name="isLanguage">Language-token probe (injected for testability).</param>
    public static string? NormalizeFileName(string fileName, Func<string, bool> isLanguage)
    {
        if (!fileName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var stem = fileName[..^4];
        var tokens = stem.Split('.');
        if (tokens.Length < 2)
        {
            return null;
        }

        var last = tokens[^1];
        string[] reordered;

        if (Captions.Contains(last))
        {
            // "X.th.hi.srt": caption last, language one before → swap them.
            if (tokens.Length < 3 || !isLanguage(tokens[^2]))
            {
                return null; // caption-only file without a language tag
            }

            reordered = [.. tokens[..^2], tokens[^1], tokens[^2]];
        }
        else if (isLanguage(last))
        {
            return null; // language already last
        }
        else if (isLanguage(tokens[^2]) && !Captions.Contains(tokens[^2]))
        {
            // "X.th.synced.srt": junk suffix after the language → move it before.
            reordered = [.. tokens[..^2], tokens[^1], tokens[^2]];
        }
        else if (tokens.Length >= 3
                 && Captions.Contains(tokens[^2])
                 && isLanguage(tokens[^3]))
        {
            // "X.th.hi.synced.srt": junk after caption+language → caption, junk, language.
            reordered = [.. tokens[..^3], tokens[^2], tokens[^1], tokens[^3]];
        }
        else
        {
            return null;
        }

        var newName = string.Join('.', reordered) + ".srt";
        return string.Equals(newName, fileName, StringComparison.Ordinal) ? null : newName;
    }
}
