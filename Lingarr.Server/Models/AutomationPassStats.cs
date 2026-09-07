namespace Lingarr.Server.Models;

/// <summary>
/// Aggregated counters for one automation pass (movies or episodes).
/// </summary>
/// <param name="New">New translation requests created.</param>
/// <param name="Scanned">Media items evaluated.</param>
/// <param name="Total">Media items eligible for this pass (IncludeInTranslation only).</param>
/// <param name="NoSubtitles">No subtitle files matched.</param>
/// <param name="NoSource">No file matches the configured source languages.</param>
/// <param name="Unknown">Files exist but carry no usable language tag.</param>
/// <param name="UpToDate">Every target already present or requested.</param>
/// <param name="TooRecent">Skipped by the age threshold.</param>
/// <param name="Excluded">Library items not evaluated because IncludeInTranslation is off.</param>
public record AutomationPassStats(
    int New,
    int Scanned,
    int Total,
    int NoSubtitles,
    int NoSource,
    int Unknown,
    int UpToDate,
    int TooRecent,
    int Excluded = 0)
{
    public static AutomationPassStats operator +(AutomationPassStats left, AutomationPassStats right) =>
        new(
            left.New + right.New,
            left.Scanned + right.Scanned,
            left.Total + right.Total,
            left.NoSubtitles + right.NoSubtitles,
            left.NoSource + right.NoSource,
            left.Unknown + right.Unknown,
            left.UpToDate + right.UpToDate,
            left.TooRecent + right.TooRecent,
            left.Excluded + right.Excluded);
}
