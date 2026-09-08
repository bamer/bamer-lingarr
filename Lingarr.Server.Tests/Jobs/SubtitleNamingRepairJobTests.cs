using Lingarr.Server.Jobs;
using Lingarr.Server.Services;
using Xunit;

namespace Lingarr.Server.Tests.Jobs;

/// <summary>
/// Tests for the pure filename normalization rule: the TARGET LANGUAGE must be
/// the final segment so Jellyfin/Emby display the right language track.
/// Covers caption-after-language files (".th.hi.srt") and junk suffixes
/// (".th.synced.srt" from VLC).
/// </summary>
public class SubtitleNamingRepairJobTests
{
    private static string? Normalize(string fileName)
        => SubtitleNamingRepairJob.NormalizeFileName(fileName, new LanguageCodeService().Validate);

    [Theory]
    [InlineData("Movie.th.hi.srt", "Movie.hi.th.srt")]
    [InlineData("Movie.en.hi.srt", "Movie.hi.en.srt")]
    [InlineData("Movie (2020) - [Bluray-1080p]-RARBG.th.hi.srt",
                "Movie (2020) - [Bluray-1080p]-RARBG.hi.th.srt")]
    [InlineData("Moana (2026) - [WEBRip-720p].th.hi.srt", "Moana (2026) - [WEBRip-720p].hi.th.srt")]
    public void Normalize_CaptionAfterLanguage_SwapsToLanguageLast(string input, string expected)
    {
        Assert.Equal(expected, Normalize(input));
    }

    [Theory]
    [InlineData("Movie.th.synced.srt", "Movie.synced.th.srt")]
    [InlineData("Movie (2008) - [Bluray-1080p]-iVy.th.synced.srt",
                "Movie (2008) - [Bluray-1080p]-iVy.synced.th.srt")]
    public void Normalize_VlcSyncedSuffix_MovesJunkBeforeLanguage(string input, string expected)
    {
        Assert.Equal(expected, Normalize(input));
    }

    [Fact]
    public void Normalize_JunkAfterCaptionAndLanguage_ReordersAllTags()
    {
        Assert.Equal("Movie.hi.synced.th.srt", Normalize("Movie.th.hi.synced.srt"));
    }

    [Theory]
    [InlineData("Movie.th.srt")]
    [InlineData("Movie.hi.th.srt")]
    [InlineData("Movie (2020).1.th.srt")]
    [InlineData("Movie.synced.th.srt")]
    public void Normalize_LanguageAlreadyLast_ReturnsNull(string input)
    {
        Assert.Null(Normalize(input));
    }

    [Theory]
    [InlineData("Movie.srt")]
    [InlineData("Movie.hi.srt")]        // caption-only, no language tag
    [InlineData("Movie.forced.srt")]    // caption-only, no language tag
    public void Normalize_NoLanguageTag_ReturnsNull(string input)
    {
        Assert.Equal(null, Normalize(input));
    }

    [Fact]
    public void Normalize_UnknownJunkSuffix_ReturnsNull()
    {
        // No language anywhere in the trailing tags — leave for the AI detector.
        Assert.Null(Normalize("Movie.readme.srt"));
    }

    [Fact]
    public void Normalize_TitleCollidingWithIsoCode_IsNotEaten()
    {
        // "It" (2017) — trailing-tag parsing must not treat the basename as Italian;
        // here the trailing tags are th+hi, basename untouched.
        Assert.Equal("It (2017).hi.th.srt", Normalize("It (2017).th.hi.srt"));
    }
}
