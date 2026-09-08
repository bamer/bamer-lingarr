using System;
using Lingarr.Core.Enum;
using Lingarr.Server.Services;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Tests for the pure target-language decision extracted from
/// MediaSubtitleProcessor.ProcessSubtitles, including the reported scenario:
/// en/fr sources on disk, Thai missing, automation never re-queueing it.
/// </summary>
public class TranslationTargetPolicyTests
{
    private static TranslationTargetPolicy.ExistingRequest Request(
        string target,
        TranslationStatus status,
        string? translatedSubtitle = null) => new(target, status, translatedSubtitle);

    [Fact]
    public void Resolve_NoRequests_QueuesEveryMissingTarget()
    {
        // Airplane II: en + fr on disk, th missing, no request ever created.
        var decision = TranslationTargetPolicy.Resolve(
            new[] { "th" },
            Array.Empty<TranslationTargetPolicy.ExistingRequest>(),
            _ => false);

        Assert.Equal(new[] { "th" }, decision.LanguagesToTranslate);
        Assert.Empty(decision.BlockedByRequests);
    }

    [Fact]
    public void Resolve_ActiveRequestBlocksItsTargetOnly()
    {
        // A stuck/queued th request blocks th but not a second missing target.
        var decision = TranslationTargetPolicy.Resolve(
            new[] { "th", "nl" },
            new[] { Request("th", TranslationStatus.Pending) },
            _ => false);

        Assert.Equal(new[] { "nl" }, decision.LanguagesToTranslate);
        Assert.Equal(new[] { "th" }, decision.BlockedByRequests);
    }

    [Fact]
    public void Resolve_InProgressRequestBlocksItsTarget()
    {
        var decision = TranslationTargetPolicy.Resolve(
            new[] { "th" },
            new[] { Request("th", TranslationStatus.InProgress) },
            _ => false);

        Assert.Empty(decision.LanguagesToTranslate);
        Assert.Equal(new[] { "th" }, decision.BlockedByRequests);
    }

    [Fact]
    public void Resolve_CompletedRequestWithoutOutputFile_RequeuesTarget()
    {
        // Completed request whose output file was deleted (or never written):
        // the translation must be redone, exactly the stuck-Thai failure mode.
        var decision = TranslationTargetPolicy.Resolve(
            new[] { "th" },
            new[] { Request("th", TranslationStatus.Completed, "/movies/m/movie.th.srt") },
            _ => false);

        Assert.Equal(new[] { "th" }, decision.LanguagesToTranslate);
        Assert.Empty(decision.BlockedByRequests);
    }

    [Fact]
    public void Resolve_CompletedRequestWithOutputFileOnDisk_BlocksTarget()
    {
        var decision = TranslationTargetPolicy.Resolve(
            new[] { "th" },
            new[] { Request("th", TranslationStatus.Completed, "/movies/m/movie.th.srt") },
            path => path.EndsWith(".th.srt"));

        Assert.Empty(decision.LanguagesToTranslate);
        Assert.Equal(new[] { "th" }, decision.BlockedByRequests);
    }

    [Fact]
    public void Resolve_CompletedRequestWithEmptyOutputPath_RequeuesTarget()
    {
        var decision = TranslationTargetPolicy.Resolve(
            new[] { "th" },
            new[] { Request("th", TranslationStatus.Completed, null) },
            _ => false);

        Assert.Equal(new[] { "th" }, decision.LanguagesToTranslate);
        Assert.Empty(decision.BlockedByRequests);
    }

    [Theory]
    [InlineData(TranslationStatus.Failed)]
    [InlineData(TranslationStatus.Cancelled)]
    [InlineData(TranslationStatus.Interrupted)]
    [InlineData(TranslationStatus.Partial)]
    public void Resolve_TerminalNonSatisfyingStatus_DoesNotBlockTarget(TranslationStatus status)
    {
        var decision = TranslationTargetPolicy.Resolve(
            new[] { "th" },
            new[] { Request("th", status, "/movies/m/movie.th.srt") },
            _ => false);

        Assert.Equal(new[] { "th" }, decision.LanguagesToTranslate);
        Assert.Empty(decision.BlockedByRequests);
    }

    [Fact]
    public void Resolve_PartialStatus_DoesNotBlockTarget()
    {
        // Partial keeps failed lines for the retry path; it never blocks the
        // target (unchanged from the pre-policy behavior).
        var outputPath = "/movies/m/movie.th.srt";
        var withoutFile = TranslationTargetPolicy.Resolve(
            new[] { "th" },
            new[] { Request("th", TranslationStatus.Partial, outputPath) },
            _ => false);

        var withFile = TranslationTargetPolicy.Resolve(
            new[] { "th" },
            new[] { Request("th", TranslationStatus.Partial, outputPath) },
            _ => true);

        Assert.Equal(new[] { "th" }, withoutFile.LanguagesToTranslate);
        Assert.Equal(new[] { "th" }, withFile.LanguagesToTranslate);
    }

    [Fact]
    public void Resolve_RequestForAnotherTarget_DoesNotBlock()
    {
        var decision = TranslationTargetPolicy.Resolve(
            new[] { "th" },
            new[] { Request("nl", TranslationStatus.InProgress) },
            _ => false);

        Assert.Equal(new[] { "th" }, decision.LanguagesToTranslate);
        Assert.Empty(decision.BlockedByRequests);
    }
}
