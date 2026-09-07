using System.Globalization;
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
/// One-shot maintenance, launched on demand from the Schedule page.
/// Scans media directories for .srt files that are 0-byte or unparseable.
/// If a matching TranslationRequest exists → rebuilds from DB lines + source.
/// If not → deletes the corpse (it falsely signals "translation done").
/// </summary>
public class SubtitleRepairJob
{
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

        var directories = await _dbContext.Movies
                .Where(m => m.Path != null)
                .Select(m => m.Path!)
                .Concat(_dbContext.Episodes
                    .Where(e => e.Path != null)
                    .Select(e => e.Path!))
                .Distinct()
                .ToListAsync();

        // Load all requests with a translated subtitle path for lookup.
        var requests = await _dbContext.TranslationRequests
            .Where(r => r.TranslatedSubtitle != null)
            .Select(r => new { r.Id, r.SubtitleToTranslate, r.TranslatedSubtitle })
            .ToListAsync();

        // Load all translation lines grouped by request for rebuild data.
        var allLines = await _dbContext.TranslationRequestLines
            .Select(l => new { l.TranslationRequestId, l.Position, l.Target })
            .ToListAsync();

        var linesByRequest = allLines
            .GroupBy(l => l.TranslationRequestId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(l => l.Position)
                    .ToDictionary(p => p.Key, p => p.Last().Target));

        var requestByPath = new Dictionary<string, (int Id, string? SourcePath)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in requests)
        {
            if (!string.IsNullOrEmpty(r.TranslatedSubtitle))
                requestByPath[r.TranslatedSubtitle] = (r.Id, r.SubtitleToTranslate);
        }

        _logger.LogInformation(
            "Subtitle repair: {DirCount} directories, {RequestCount} tracked translations.",
            directories.Count, requestByPath.Count);

        var scanned = 0;
        var repaired = 0;
        var deleted = 0;
        var healthy = 0;
        var skipped = 0;

        foreach (var directory in directories)
        {
            string[] files;
            try { files = Directory.GetFiles(directory, "*.srt", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in files)
            {
                scanned++;

                // Check if file parses to something.
                List<SubtitleItem> parsed;
                try { parsed = await _subtitleService.ReadSubtitles(file); }
                catch { parsed = []; }

                if (parsed.Count > 0)
                {
                    healthy++;
                    continue;
                }

                // File is broken. Is it a known translation output?
                if (!requestByPath.TryGetValue(file, out var req))
                {
                    // Not in DB → 0-byte orphan, delete it.
                    DeleteCorpse(file, "no translation in database");
                    deleted++;
                    continue;
                }

                // Recently written? Skip — may be mid-flight.
                try
                {
                    if (File.Exists(file) &&
                        DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < RecentWriteGrace)
                    {
                        skipped++;
                        continue;
                    }
                }
                catch { continue; }

                // Rebuild from DB lines + source subtitle.
                if (!linesByRequest.TryGetValue(req.Id, out var lines) || lines.Count == 0)
                {
                    DeleteCorpse(file, $"request {req.Id} has no lines in database");
                    deleted++;
                    continue;
                }

                var usable = lines.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                if (usable.Count == 0)
                {
                    DeleteCorpse(file, $"request {req.Id} has no usable lines");
                    deleted++;
                    continue;
                }

                if (string.IsNullOrEmpty(req.SourcePath))
                {
                    DeleteCorpse(file, $"request {req.Id} has no source path");
                    deleted++;
                    continue;
                }

                List<SubtitleItem> sourceItems;
                try { sourceItems = await _subtitleService.ReadSubtitles(req.SourcePath); }
                catch
                {
                    DeleteCorpse(file, $"source {req.SourcePath} unreadable");
                    deleted++;
                    continue;
                }

                if (sourceItems.Count == 0)
                {
                    DeleteCorpse(file, $"source {req.SourcePath} is empty");
                    deleted++;
                    continue;
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
                    await _subtitleService.WriteSubtitles(file, rebuilt, stripSubtitleFormatting: false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Subtitle repair: write failed for {File}.", file);
                    continue;
                }

                List<SubtitleItem> verify;
                try { verify = await _subtitleService.ReadSubtitles(file); }
                catch { verify = []; }

                if (verify.Count == 0)
                {
                    _logger.LogWarning("Subtitle repair: rebuilt {File} still empty.", file);
                    continue;
                }

                repaired++;
                _logger.LogInformation(
                    "Subtitle repair: rebuilt {File} with {Cues} cues (request {RequestId}).",
                    file, verify.Count, req.Id);
            }
        }

        _logger.LogInformation(
            "Subtitle repair complete: {Scanned} scanned, {Healthy} healthy, {Repaired} rebuilt, {Deleted} deleted, {Skipped} skipped.",
            scanned, healthy, repaired, deleted, skipped);
        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
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
