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
/// summary; never touches healthy files or files without DB data to rebuild from.
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

        // Scan directories come from Movies + Episodes (permanent data).
        var directories = await _dbContext.Movies
                .Where(m => m.Path != null)
                .Select(m => m.Path!)
                .Concat(_dbContext.Episodes
                    .Where(e => e.Path != null)
                    .Select(e => e.Path!))
                .Distinct()
                .ToListAsync();

        // Load request metadata (paths) and lines separately, then merge.
        // The join alone drops requests with 0 lines — those still need their
        // SubtitleToTranslate / TranslatedSubtitle registered.
        var requests = await _dbContext.TranslationRequests
            .Where(r => r.SubtitleToTranslate != null || r.TranslatedSubtitle != null)
            .Select(r => new { r.Id, r.SubtitleToTranslate, r.TranslatedSubtitle })
            .ToListAsync();

        var linesQuery = await (
            from line in _dbContext.TranslationRequestLines
            join req in _dbContext.TranslationRequests
                on line.TranslationRequestId equals req.Id
            select new { req.Id, line.Position, line.Target })
            .ToListAsync();

        // Build: filePath → (requestId, isTarget)
        var pathToRequest = new Dictionary<string, RepairContext>(StringComparer.OrdinalIgnoreCase);
        // Build: requestId → lines
        var requestLines = new Dictionary<int, Dictionary<int, string>>();
        // Build: requestId → source path
        var requestSources = new Dictionary<int, string>();

        foreach (var r in requests)
        {
            requestSources[r.Id] = r.SubtitleToTranslate ?? "";

            if (!string.IsNullOrEmpty(r.SubtitleToTranslate))
                pathToRequest[r.SubtitleToTranslate] = new RepairContext { RequestId = r.Id, IsTarget = false };

            if (!string.IsNullOrEmpty(r.TranslatedSubtitle))
                pathToRequest[r.TranslatedSubtitle] = new RepairContext { RequestId = r.Id, IsTarget = true };
        }

        foreach (var row in linesQuery)
        {
            if (!requestLines.ContainsKey(row.Id))
                requestLines[row.Id] = new Dictionary<int, string>();
            requestLines[row.Id][row.Position] = row.Target;
        }

        _logger.LogInformation(
            "Subtitle repair: {DirCount} directories, {PathCount} tracked paths.",
            directories.Count, pathToRequest.Count);

        var scanned = 0;
        var repaired = 0;
        var deleted = 0;
        var skippedRecent = 0;
        var healthy = 0;
        var untracked = 0;

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

                List<SubtitleItem> parsed;
                try
                {
                    parsed = await _subtitleService.ReadSubtitles(file);
                }
                catch
                {
                    parsed = [];
                }

                if (parsed.Count > 0)
                {
                    healthy++;
                    continue;
                }

                // File is broken (0 cues). Can we rebuild it?
                if (!pathToRequest.TryGetValue(file, out var ctx))
                {
                    untracked++;
                    continue; // No DB data → leave untouched
                }

                // Recently written? Skip — may be mid-flight.
                try
                {
                    var info = new FileInfo(file);
                    if (DateTime.UtcNow - info.LastWriteTimeUtc < RecentWriteGrace)
                    {
                        skippedRecent++;
                        continue;
                    }
                }
                catch
                {
                    // File vanished between parse and stat — skip.
                    continue;
                }

                if (!ctx.IsTarget)
                {
                    DeleteCorpse(file, $"source file broken, cannot rebuild (request {ctx.RequestId})");
                    deleted++;
                    continue;
                }

                if (!requestLines.TryGetValue(ctx.RequestId, out var lines) ||
                    !requestSources.TryGetValue(ctx.RequestId, out var sourcePath))
                {
                    untracked++;
                    continue;
                }

                var usable = lines
                    .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                if (usable.Count == 0)
                {
                    DeleteCorpse(file, $"request {ctx.RequestId} has no usable lines");
                    deleted++;
                    continue;
                }

                List<SubtitleItem> sourceItems;
                try
                {
                    sourceItems = await _subtitleService.ReadSubtitles(sourcePath);
                }
                catch
                {
                    DeleteCorpse(file, $"source {sourcePath} unreadable");
                    deleted++;
                    continue;
                }

                if (sourceItems.Count == 0)
                {
                    DeleteCorpse(file, $"source {sourcePath} is empty");
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
                    _logger.LogWarning(ex, "Subtitle repair: rebuild of {File} failed.", file);
                    continue;
                }

                List<SubtitleItem> verify;
                try
                {
                    verify = await _subtitleService.ReadSubtitles(file);
                }
                catch
                {
                    verify = [];
                }

                if (verify.Count == 0)
                {
                    _logger.LogWarning("Subtitle repair: rebuilt {File} still parses to zero cues.", file);
                    continue;
                }

                repaired++;
                _logger.LogInformation(
                    "Subtitle repair: rebuilt {File} with {Cues} cues from request {RequestId}.",
                    file, verify.Count, ctx.RequestId);
            }
        }

        _logger.LogInformation(
            "Subtitle repair complete: {Scanned} scanned, {Healthy} healthy, {Repaired} rebuilt, {Deleted} deleted, {Skipped} skipped (recent), {Untracked} untracked.",
            scanned, healthy, repaired, deleted, skippedRecent, untracked);
        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
    }

    private sealed class RepairContext
    {
        public int RequestId { get; init; }
        public bool IsTarget { get; init; }
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
