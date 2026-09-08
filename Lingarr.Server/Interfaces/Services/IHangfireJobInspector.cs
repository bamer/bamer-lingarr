namespace Lingarr.Server.Interfaces.Services;

/// <summary>
/// Read-only view over Hangfire's storage, kept behind an interface so the
/// stale-request reconciliation can be unit tested without a live Hangfire server.
/// </summary>
public interface IHangfireJobInspector
{
    /// <summary>
    /// Returns the current Hangfire state name of a job ("Enqueued", "Processing", ...),
    /// or null when the job no longer exists in storage (crash, purge, retention).
    /// </summary>
    string? GetJobState(string jobId);
}
