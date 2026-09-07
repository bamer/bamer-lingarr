using Hangfire;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Filters;
using Lingarr.Server.Interfaces.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;

namespace Lingarr.Server.Jobs;

public class CleanupJob
{
    private readonly LingarrDbContext _dbContext;
    private readonly ILogger<CleanupJob> _logger;
    private readonly IScheduleService _scheduleService;

    public CleanupJob(
        LingarrDbContext dbContext,
        IScheduleService scheduleService,
        ILogger<CleanupJob> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
        _scheduleService = scheduleService;
    }

    [AutomaticRetry(Attempts = 0)]
    [Queue("system")]
    public async Task Execute()
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

        var oneWeekAgo = DateTime.UtcNow.AddDays(-7);
        // ponytail: only terminal requests are history. Deleting Pending/InProgress
        // rows silently dropped queued work every week (translations vanished
        // "without reason" when the backlog was slower than the cleanup cycle).
        var terminalStatuses = new[]
        {
            TranslationStatus.Completed,
            TranslationStatus.Failed,
            TranslationStatus.Cancelled,
            TranslationStatus.Interrupted,
            TranslationStatus.Partial
        };
        var oldJobs = await _dbContext.TranslationRequests
            .Where(pg => pg.CreatedAt < oneWeekAgo && terminalStatuses.Contains(pg.Status))
            .ToListAsync();

        // Protect requests whose translated file is broken (0-byte or missing).
        // The SubtitleRepairJob needs these rows to rebuild the file from DB data.
        // Once the repair job fixes (or confirms unrepairable) the file, it will
        // mark the request as Interrupted so cleanup can finally remove it.
        var protectedJobs = new List<Core.Entities.TranslationRequest>();
        var jobsToDelete = new List<Core.Entities.TranslationRequest>();
        foreach (var job in oldJobs)
        {
            if (string.IsNullOrEmpty(job.TranslatedSubtitle))
            {
                jobsToDelete.Add(job);
                continue;
            }
            try
            {
                var info = new FileInfo(job.TranslatedSubtitle);
                if (!info.Exists || info.Length == 0)
                {
                    protectedJobs.Add(job);
                }
                else
                {
                    jobsToDelete.Add(job);
                }
            }
            catch
            {
                jobsToDelete.Add(job);
            }
        }

        var pendingProtected = await _dbContext.TranslationRequests
            .Where(pg => pg.CreatedAt < oneWeekAgo && !terminalStatuses.Contains(pg.Status))
            .CountAsync();
        var totalProtected = protectedJobs.Count + pendingProtected;
        if (totalProtected > 0)
        {
            _logger.LogWarning(
                "{Count} translation requests kept: {BrokenProtected} have broken translated files (repair needed), {PendingProtected} still Pending/InProgress.",
                totalProtected, protectedJobs.Count, pendingProtected);
        }

        foreach (var job in jobsToDelete)
        {
            _dbContext.TranslationRequests.Remove(job);
        }

        await _dbContext.SaveChangesAsync();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
        _logger.LogInformation("Removed {Count} old translation requests, kept {Protected} with broken files.",
            jobsToDelete.Count, protectedJobs.Count);
    }
}