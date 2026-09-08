using Hangfire;
using Hangfire.Storage;
using Lingarr.Server.Interfaces.Services;

namespace Lingarr.Server.Services;

/// <summary>
/// Hangfire-backed implementation of <see cref="IHangfireJobInspector"/>.
/// </summary>
public class HangfireJobInspector : IHangfireJobInspector
{
    /// <inheritdoc />
    public string? GetJobState(string jobId)
    {
        try
        {
            using var connection = JobStorage.Current.GetConnection();
            return connection.GetStateData(jobId)?.Name;
        }
        catch (Exception)
        {
            // Storage hiccup — report an unknown-but-alive state: the policy only
            // releases a request when storage PROVES the job can no longer run,
            // so a transient error must never free work that might be queued.
            return "Unknown";
        }
    }
}
