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
        _scheduleService = scheduleService;
        _logger = logger;
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

        var protectedJobs = await _dbContext.TranslationRequests
            .Where(pg => pg.CreatedAt < oneWeekAgo && !terminalStatuses.Contains(pg.Status))
            .CountAsync();
        if (protectedJobs > 0)
        {
            _logger.LogWarning(
                "{Count} translation requests older than a week are still Pending/InProgress and were kept.",
                protectedJobs);
        }

        foreach (var job in oldJobs)
        {
            _dbContext.TranslationRequests.Remove(job);
        }

        await _dbContext.SaveChangesAsync();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
        _logger.LogInformation($"Removed {oldJobs.Count} translation requests that are older than a week.");
    }
}
