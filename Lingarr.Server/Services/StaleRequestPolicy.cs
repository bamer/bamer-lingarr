namespace Lingarr.Server.Services;

/// <summary>
/// Pure policy deciding whether a stuck translation request may be released.
/// A request whose Hangfire job is still alive (queued behind a large backlog,
/// scheduled, or running) must NEVER be released — doing so deletes a live job
/// and re-queues the same work, and with a backlog deeper than the staleness
/// threshold this turns into an endless churn that permanently blocks the
/// target language while the queue never drains.
/// </summary>
public static class StaleRequestPolicy
{
    /// <summary>
    /// Hangfire states proving the job can never run (or run again): gone (null),
    /// succeeded without the status being advanced, failed without retries,
    /// deleted or expired. Everything else — Enqueued, Scheduled, Processing,
    /// Awaiting, or any unknown state — is treated as alive so a storage hiccup
    /// or a future Hangfire version can never free work that might still run.
    /// </summary>
    public static readonly IReadOnlySet<string> DeadJobStates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Succeeded",
            "Failed",
            "Deleted",
            "Expired"
        };

    /// <param name="createdAtUtc">When the translation request was created.</param>
    /// <param name="nowUtc">Current time (UTC).</param>
    /// <param name="staleHours">Age threshold beyond which a request is considered stuck.</param>
    /// <param name="jobState">Current Hangfire state of the request's job, or null when gone.</param>
    /// <returns>True when the request is old enough AND its job can no longer run.</returns>
    public static bool IsReleasable(
        DateTime createdAtUtc,
        DateTime nowUtc,
        int staleHours,
        string? jobState)
    {
        if (staleHours <= 0)
        {
            return false;
        }

        if (nowUtc - createdAtUtc < TimeSpan.FromHours(staleHours))
        {
            return false;
        }

        return jobState is null || DeadJobStates.Contains(jobState);
    }
}
