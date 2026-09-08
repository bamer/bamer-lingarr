using Lingarr.Core.Enum;

namespace Lingarr.Server.Services;

/// <summary>
/// Pure decision core for "which target languages still need work".
/// Extracted from <see cref="MediaSubtitleProcessor.ProcessSubtitles"/> so the
/// rules are unit-testable without EF, the file system or Hangfire.
/// </summary>
public static class TranslationTargetPolicy
{
    /// <summary>One TranslationRequest row relevant to a target-language decision.</summary>
    /// <param name="TargetLanguage">Language the request translates towards.</param>
    /// <param name="Status">Current request status.</param>
    /// <param name="TranslatedSubtitle">Output file path recorded on completion, if any.</param>
    public sealed record ExistingRequest(
        string TargetLanguage,
        TranslationStatus Status,
        string? TranslatedSubtitle);

    /// <param name="LanguagesToTranslate">Targets that must be queued now.</param>
    /// <param name="BlockedByRequests">Targets missing on disk but already covered by a request.</param>
    public sealed record TargetDecision(
        IReadOnlyList<string> LanguagesToTranslate,
        IReadOnlyList<string> BlockedByRequests);

    /// <summary>
    /// Decides, per target language that is missing from disk, whether work must be
    /// queued or the target is already covered by an existing request:
    /// - a Pending/InProgress request always blocks (work is queued or running);
    /// - a Completed request blocks only while its output file still exists — if the
    ///   file was deleted (or never written) the translation must be redone;
    /// - anything else (Failed, Cancelled, Interrupted, Partial, or no request at
    ///   all) leaves the target eligible for a fresh request.
    /// </summary>
    /// <param name="targetsMissingFromDisk">Target languages with no satisfying subtitle file.</param>
    /// <param name="existingRequests">Requests recorded for this media.</param>
    /// <param name="outputFileExists">File existence probe (injected for testability).</param>
    public static TargetDecision Resolve(
        IEnumerable<string> targetsMissingFromDisk,
        IEnumerable<ExistingRequest> existingRequests,
        Func<string, bool> outputFileExists)
    {
        var requestsByTarget = existingRequests
            .GroupBy(request => request.TargetLanguage)
            .ToDictionary(group => group.Key, group => group.ToList());

        var toTranslate = new List<string>();
        var blocked = new List<string>();
        foreach (var target in targetsMissingFromDisk)
        {
            var requests = requestsByTarget.GetValueOrDefault(target);
            if (requests is not null && requests.Any(request =>
                    request.Status is TranslationStatus.Pending or TranslationStatus.InProgress))
            {
                blocked.Add(target);
                continue;
            }

            if (requests is not null && requests.Any(request =>
                    request.Status == TranslationStatus.Completed
                    && !string.IsNullOrEmpty(request.TranslatedSubtitle)
                    && outputFileExists(request.TranslatedSubtitle)))
            {
                blocked.Add(target);
                continue;
            }

            toTranslate.Add(target);
        }

        return new TargetDecision(toTranslate, blocked);
    }
}
