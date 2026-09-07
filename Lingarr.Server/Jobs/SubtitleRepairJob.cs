using Hangfire;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Filters;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.FileSystem;
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

        var directories = await _dbContext.Movies
                .Where(movie => movie.Path != null)
                .Select(movie => movie.Path!)
                .Concat(_dbContext.Episodes
                    .Where(episode => episode.Path != null)
                    .Select(episode => episode.Path!))
                .Distinct()
                .ToListAsync();

        var scanned = 0;
        var repaired = 0;
        var deleted = 0;
        var skippedRecent = 0;
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
                switch (await RepairFileAsync(file))
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
                }
            }
        }

        _logger.LogInformation(
            "Subtitle repair complete: {Scanned} files scanned, {Repaired} rebuilt from the database, {Deleted} corpses deleted, {Skipped} skipped (recently written).",
            scanned, repaired, deleted, skippedRecent);
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

    private async Task<RepairOutcome> RepairFileAsync(string filePath)
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
                _logger.LogDebug("Subtitle repair: skipping recently written file {File}.", filePath);
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

        _logger.LogWarning("Subtitle repair: {File} parses to zero cues.", filePath);

        var request = await _dbContext.TranslationRequests
            .Where(translationRequest => translationRequest.TranslatedSubtitle == filePath)
            .OrderByDescending(translationRequest => translationRequest.Id)
            .FirstOrDefaultAsync();
        if (request == null)
        {
            DeleteCorpse(filePath, "no translation in the database");
            return RepairOutcome.Deleted;
        }

        var targetsByPosition = (await _dbContext.TranslationRequestLines
                .Where(line => line.TranslationRequestId == request.Id)
                .ToListAsync())
            .GroupBy(line => line.Position)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(line => line.Id).First().Target);
        var usable = targetsByPosition
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        if (usable.Count == 0)
        {
            DeleteCorpse(filePath, $"request {request.Id} has no usable lines in the database");
            return RepairOutcome.Deleted;
        }

        List<SubtitleItem> sourceItems;
        try
        {
            sourceItems = string.IsNullOrEmpty(request.SubtitleToTranslate)
                ? []
                : await _subtitleService.ReadSubtitles(request.SubtitleToTranslate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Subtitle repair: source {Source} for request {RequestId} is unreadable, deleting {File}.",
                request.SubtitleToTranslate, request.Id, filePath);
            DeleteCorpse(filePath, "source subtitle is gone");
            return RepairOutcome.Deleted;
        }

        if (sourceItems.Count == 0)
        {
            _logger.LogWarning(
                "Subtitle repair: source {Source} for request {RequestId} is empty, deleting {File}.",
                request.SubtitleToTranslate, request.Id, filePath);
            DeleteCorpse(filePath, "source subtitle is empty");
            return RepairOutcome.Deleted;
        }

        // Missing positions fall back to the source text (same rule as batch retries),
        // so the rebuilt file stays valid and the request keeps its Partial status.
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
            filePath, verify.Count, request.Id);
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
