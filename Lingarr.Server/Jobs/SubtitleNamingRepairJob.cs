using Hangfire;
using Lingarr.Core.Configuration;
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
/// Normalizes translated subtitle file names to the Plex/Jellyfin/Emby
/// standard: language first, optional caption after —
/// "Inception.2010.fr.srt" (regular), "Inception.2010.fr.hi.srt" (HI/SDH).
/// Repairs legacy variants: the inverted order shipped by 2.27.0
/// ("Movie.hi.th.srt" → "Movie.th.hi.srt"), VLC-style junk suffixes
/// ("Movie.th.synced.srt" → "Movie.th.srt", "synced" is dropped), and moves
/// an old trailing custom tag before the language ("Movie.th.hi.lingarr.srt"
/// → "Movie.lingarr.th.hi.srt"). Renamed paths are re-pointed on translation
/// requests so nothing gets re-translated.
/// </summary>
public class SubtitleNamingRepairJob
{
    /// <summary>Caption tags recognized by Lingarr's filename parser.</summary>
    private static readonly HashSet<string> Captions = new(StringComparer.OrdinalIgnoreCase)
        { "sdh", "cc", "forced", "hi" };

    /// <summary>Junk suffixes to drop (VLC-style sync copies add ".synced").</summary>
    private static readonly HashSet<string> DropTokens = new(StringComparer.OrdinalIgnoreCase)
        { "synced", "sync" };

    private readonly LingarrDbContext _dbContext;
    private readonly ILogger<SubtitleNamingRepairJob> _logger;
    private readonly IScheduleService _scheduleService;
    private readonly LanguageCodeService _languageCodeService;
    private readonly ISettingService _settingService;

    public SubtitleNamingRepairJob(
        LingarrDbContext dbContext,
        IScheduleService scheduleService,
        LanguageCodeService languageCodeService,
        ISettingService settingService,
        ILogger<SubtitleNamingRepairJob> logger)
    {
        _dbContext = dbContext;
        _scheduleService = scheduleService;
        _languageCodeService = languageCodeService;
        _settingService = settingService;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [Queue("system")]
    public async Task Execute()
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

        // The custom subtitle tag (opt-in, default "lingarr") has been written
        // after the caption by older versions (Movie.th.hi.lingarr.srt). We move
        // it before the language — knowing its exact value lets us recognize it.
        var tag = await GetConfiguredTag();

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
                var normalized = NormalizeFileName(fileName, _languageCodeService.Validate, tag);
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
    /// Pure normalization rule. Classifies the trailing filename tokens
    /// (language, caption, junk to drop, optional custom tag) and re-emits them
    /// in the media-server standard order: optional tag, then language, then
    /// caption(s) — "Movie.lingarr.th.hi.srt". Junk tokens ("synced"/"sync")
    /// are dropped. Returns null when the name already matches the standard
    /// (or carries no language tag).
    /// </summary>
    /// <param name="fileName">File name with extension, e.g. "Movie.hi.th.srt".</param>
    /// <param name="isLanguage">Language-token probe (injected for testability).</param>
    /// <param name="tag">Configured custom subtitle tag, or null when tagging is off.</param>
    public static string? NormalizeFileName(string fileName, Func<string, bool> isLanguage, string? tag = null)
    {
        if (!fileName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tokens = fileName[..^4].Split('.');
        if (tokens.Length < 2)
        {
            return null;
        }

        // Walk backwards while the token is one of our trailing tags.
        // "hi" is BOTH a caption tag and the ISO code for Hindi — the parser
        // treats it as a caption first (a lone ".hi" is ambiguous), so the
        // language check never applies to caption tokens.
        bool IsCaption(string token) => Captions.Contains(token);
        bool IsLanguageToken(string token) => isLanguage(token) && !IsCaption(token);

        var trailing = new List<string>();
        var idx = tokens.Length - 1;
        while (idx >= 0)
        {
            var token = tokens[idx];
            if (IsCaption(token)
                || IsLanguageToken(token)
                || DropTokens.Contains(token)
                || (tag != null && string.Equals(token, tag, StringComparison.OrdinalIgnoreCase)))
            {
                trailing.Add(token); // collected in reverse order
                idx--;
                continue;
            }
            break;
        }

        var hadLanguage = trailing.Any(IsLanguageToken);
        if (!hadLanguage)
        {
            return null; // no language tag → leave the file alone
        }

        var original = trailing.AsEnumerable().Reverse().ToList(); // original tail order
        var tagFound = tag != null && original.Any(token => string.Equals(token, tag, StringComparison.OrdinalIgnoreCase));
        var language = original.Last(IsLanguageToken);

        // Standard order: [tag?] [language] [caption...] — junk is dropped.
        var canonical = new List<string>();
        if (tagFound)
        {
            canonical.Add(tag!);
        }
        canonical.Add(language);
        foreach (var token in original)
        {
            if (Captions.Contains(token))
            {
                canonical.Add(token);
            }
        }

        var stemEnd = idx + 1;
        var newParts = new List<string>();
        for (var i = 0; i < stemEnd; i++)
        {
            newParts.Add(tokens[i]);
        }
        foreach (var part in canonical)
        {
            newParts.Add(part);
        }
        var newName = string.Join('.', newParts) + ".srt";
        return string.Equals(newName, fileName, StringComparison.Ordinal) ? null : newName;
    }

    private async Task<string?> GetConfiguredTag()
    {
        try
        {
            if (await _settingService.GetSetting(SettingKeys.Translation.UseSubtitleTagging) != "true")
            {
                return null;
            }

            var tag = await _settingService.GetSetting(SettingKeys.Translation.SubtitleTag);
            return string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
