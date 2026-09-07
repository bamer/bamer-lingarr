using Hangfire;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Filters;
using Lingarr.Server.Interfaces.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;

namespace Lingarr.Server.Jobs;

/// <summary>
/// One-shot maintenance, triggered manually from the Schedule page: finds subtitle
/// files that parse to zero cues (0-byte or all-blank) and either rebuilds them
/// from the translation lines in the database or deletes the corpse. Logs a
/// summary; never touches healthy files.
/// </summary>
public class SubtitleRepairJob
{
    // A file modified moments ago may be mid-write by a running translation.
    private static readonly TimeSpan RecentWriteGrace = TimeSpan.FromMinutes(10);

    private readonly LingarrDbContext _dbContext;
    private readonly ILogger<SubtitleRepairJob> _logger;
    private readonly IScheduleService _scheduleService;
    private readonly ISubtitleService _subtitleService;

    public SubtitleRepairJob(
        LingarrDbContext dbContext,
        IScheduleService scheduleService,
        ISubtitleService subtitleService,
        ILogger<SubtitleRepairJob> logger)
    {
        _dbContext = dbContext;
        _scheduleService = scheduleService;
        _subtitleService = subtitleService;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [Queue("system")]
    public async Task Execute()
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

        // Derive scan directories from the translation requests themselves:
        // every SubtitleToTranslate/TranslatedSubtitle path tells us where the
        // media folder is.  This avoids depending on Movies/Episodes tables.
        var allPaths = await _dbContext.TranslationRequests
            .Where(r => r.SubtitleToTranslate != null || r.TranslatedSubtitle != null)
            .Select(r => new { r.SubtitleToTranslate, r.TranslatedSubtitle })
            .ToListAsync();

        var directories = allPaths
            .SelectMany(r => new[] { r.SubtitleToTranslate, r.TranslatedSubtitle })
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetDirectoryName(p)!)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .ToList();

        // Pre-load all translation request paths for fast in-memory lookup.
        var knownPaths = await _dbContext.TranslationRequests
            .Where(r => r.SubtitleToTranslate != null || r.TranslatedSubtitle != null)
            .Select(r => new { r.Id, r.SubtitleToTranslate, r.TranslatedSubtitle })
            .ToListAsync();

        // Build a map: file path → request that owns it.
        // Both SubtitleToTranslate and TranslatedSubtitle are registered so the
        // scan detects broken files regardless of source/target role.
        var pathToRequest = new Dictionary<string, (int RequestId, bool IsTarget)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in knownPaths)
        {
            if (!string.IsNullOrEmpty(r.SubtitleToTranslate))
            {
                pathToRequest[r.SubtitleToTranslate] = (r.Id, IsTarget: false);
            }
            if (!string.IsNullOrEmpty(r.TranslatedSubtitle))
            {
                // Overwrite: translated takes priority over source for the same file.
                pathToRequest[r.TranslatedSubtitle] = (r.Id, IsTarget: true);
            }
        }

        var scanned = 0;
        var repaired = 0;
        var deleted = 0;
        var skippedRecent = 0;
        var healthy = 0;
        foreach (var directory in directories)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.srt", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subtitle repair: skipping unreadable directory {Directory}.", directory);
                continue;
            }

            foreach (var file in files)
            {
                scanned++;
                if (!pathToRequest.TryGetValue(file, out var match))
                {
                    continue;
                }

                switch (await RepairFileAsync(file, match.RequestId, match.IsTarget))
                {
                    case RepairOutcome.Repaired:
                        repaired++;
                        break;
                    case RepairOutcome.Deleted:
                        deleted++;
                        break;
                    case RepairOutcome.SkippedRecent:
                        skippedRecent++;
                        break;
                    case RepairOutcome.Healthy:
                        healthy++;
                        break;
                }
            }
        }

        _logger.LogInformation(
            "Subtitle repair complete: {Scanned} files scanned, {Healthy} healthy, {Repaired} rebuilt, {Deleted} corpses deleted, {Skipped} skipped (recently written).",
            scanned, healthy, repaired, deleted, skippedRecent);
        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
    }

    private enum RepairOutcome
    {
        Healthy,
        Repaired,
        Deleted,
        SkippedRecent,
        Failed
    }

    private async Task<RepairOutcome> RepairFileAsync(string filePath, int requestId, bool isTarget)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return RepairOutcome.Healthy;
            }
            if (DateTime.UtcNow - info.LastWriteTimeUtc < RecentWriteGrace)
            {
                return RepairOutcome.SkippedRecent;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subtitle repair: cannot inspect {File}.", filePath);
            return RepairOutcome.Failed;
        }

        List<SubtitleItem> parsed;
        try
        {
            parsed = await _subtitleService.ReadSubtitles(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subtitle repair: cannot read {File}.", filePath);
            return RepairOutcome.Failed;
        }

        if (parsed.Count > 0)
        {
            return RepairOutcome.Healthy;
        }

        // Source file (SubtitleToTranslate): request lines store the translation
        // output, not the source text, so we cannot rebuild it — just delete.
        if (!isTarget)
        {
            DeleteCorpse(filePath, $"source file broken, cannot rebuild (request {requestId})");
            return RepairOutcome.Deleted;
        }

        var usable = (await _dbContext.TranslationRequestLines
                .Where(line => line.TranslationRequestId == requestId)
                .ToListAsync())
            .GroupBy(line => line.Position)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(line => line.Id).First().Target)
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        if (usable.Count == 0)
        {
            DeleteCorpse(filePath, $"request {requestId} has no usable lines in the database");
            return RepairOutcome.Deleted;
        }

        // Need the source subtitle to reconstruct timings + missing positions.
        var sourcePath = await _dbContext.TranslationRequests
            .Where(r => r.Id == requestId)
            .Select(r => r.SubtitleToTranslate)
            .FirstOrDefaultAsync();

        List<SubtitleItem> sourceItems;
        try
        {
            sourceItems = string.IsNullOrEmpty(sourcePath)
                ? []
                : await _subtitleService.ReadSubtitles(sourcePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Subtitle repair: source {Source} for request {RequestId} unreadable, deleting {File}.",
                sourcePath, requestId, filePath);
            DeleteCorpse(filePath, "source subtitle is gone");
            return RepairOutcome.Deleted;
        }

        if (sourceItems.Count == 0)
        {
            DeleteCorpse(filePath, $"source subtitle for request {requestId} is empty");
            return RepairOutcome.Deleted;
        }

        var rebuilt = sourceItems.Select(source => new SubtitleItem
        {
            Position = source.Position,
            StartTime = source.StartTime,
            EndTime = source.EndTime,
            Lines = new List<string>(source.Lines),
            PlaintextLines = new List<string>(source.PlaintextLines),
            TranslatedLines = usable.TryGetValue(source.Position, out var target)
                ? [target]
                : new List<string>(source.Lines)
        }).ToList();

        try
        {
            await _subtitleService.WriteSubtitles(filePath, rebuilt, stripSubtitleFormatting: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subtitle repair: rebuild of {File} failed.", filePath);
            return RepairOutcome.Failed;
        }

        List<SubtitleItem> verify;
        try
        {
            verify = await _subtitleService.ReadSubtitles(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subtitle repair: cannot verify rebuilt {File}.", filePath);
            return RepairOutcome.Failed;
        }

        if (verify.Count == 0)
        {
            _logger.LogWarning("Subtitle repair: rebuilt {File} still parses to zero cues.", filePath);
            return RepairOutcome.Failed;
        }

        _logger.LogInformation(
            "Subtitle repair: rebuilt {File} with {Cues} cues from request {RequestId}.",
            filePath, verify.Count, requestId);
        return RepairOutcome.Repaired;
    }

    private void DeleteCorpse(string filePath, string reason)
    {
        try
        {
            File.Delete(filePath);
            _logger.LogInformation("Subtitle repair: deleted {File} ({Reason}).", filePath, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subtitle repair: could not delete {File}.", filePath);
        }
    }
}
