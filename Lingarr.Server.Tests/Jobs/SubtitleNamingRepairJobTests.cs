using Lingarr.Server.Jobs;
using Lingarr.Server.Services;
using Xunit;

namespace Lingarr.Server.Tests.Jobs;

/// <summary>
/// Tests for the pure filename normalization rule. Media servers expect the
/// standard "Movie.fr.srt" / "Movie.fr.hi.srt" (language, then optional
/// caption). Legacy variants are normalized: inverted order shipped by 2.27.0
/// (".hi.th.srt"), VLC "synced" junk (dropped), and an old trailing custom tag
/// (moved before the language). Standard names are left untouched.
/// </summary>
public class SubtitleNamingRepairJobTests
{
    private static string? Normalize(string fileName)
        => SubtitleNamingRepairJob.NormalizeFileName(fileName, new LanguageCodeService().Validate);

    [Theory]
    [InlineData("Movie.th.hi.srt", "Movie.hi.th.srt")]
    [InlineData("Movie.fr.hi.srt", "Movie.hi.fr.srt")]
    [InlineData("Movie (2020) - [Bluray-1080p]-RARBG.th.hi.srt",
                "Movie (2020) - [Bluray-1080p]-RARBG.hi.th.srt")]
    [InlineData("Moana (2026) - [WEBRip-720p].th.hi.srt", "Moana (2026) - [WEBRip-720p].hi.th.srt")]
    public void Normalize_InvertedOrder_ReturnsStandardLanguageFirst(string standard, string legacy)
    {
        // 2.27.0 wrote ".hi.th.srt"; the standard expected by media servers is
        // the language FIRST (".th.hi.srt") — so the legacy form is corrected.
        Assert.Equal(standard, Normalize(legacy));
    }

    [Theory]
    [InlineData("Movie.th.synced.srt", "Movie.th.srt")]
    [InlineData("Movie (2008) - [Bluray-1080p]-iVy.th.synced.srt",
                "Movie (2008) - [Bluray-1080p]-iVy.th.srt")]
    public void Normalize_VlcSyncedSuffix_IsDropped(string input, string expected)
    {
        Assert.Equal(expected, Normalize(input));
    }

    [Fact]
    public void Normalize_JunkAfterCaptionAndLanguage_IsDropped()
    {
        Assert.Equal("Movie.th.hi.srt", Normalize("Movie.th.hi.synced.srt"));
    }

    [Fact]
    public void Normalize_JunkBeforeLanguage_IsDropped()
    {
        Assert.Equal("Movie.fr.srt", Normalize("Movie.synced.fr.srt"));
    }

    [Fact]
    public void Normalize_OldTrailingTag_MovedBeforeLanguage()
    {
        var isLanguage = new LanguageCodeService().Validate;
        Assert.Equal("Movie.lingarr.th.hi.srt",
            SubtitleNamingRepairJob.NormalizeFileName("Movie.th.hi.lingarr.srt", isLanguage, "lingarr"));
        // Already-standard placement (tag before language) is untouched.
        Assert.Null(SubtitleNamingRepairJob.NormalizeFileName("Movie.lingarr.th.hi.srt", isLanguage, "lingarr"));
    }

    [Theory]
    [InlineData("Movie.th.srt")]
    [InlineData("Movie.th.hi.srt")]         // standard HI
    [InlineData("Movie.fr.hi.srt")]         // standard HI (any language)
    [InlineData("Movie (2020).1.th.srt")]   // multi-part counter
    [InlineData("Movie.lingarr.th.hi.srt")] // new format: tag before language
    [InlineData("It (2017).th.hi.srt")]     // title word "It" untouched
    public void Normalize_AlreadyStandard_ReturnsNull(string input)
    {
        Assert.Null(Normalize(input));
    }

    [Theory]
    [InlineData("Movie.srt")]
    [InlineData("Movie.hi.srt")]        // caption-only, no language tag
    [InlineData("Movie.forced.srt")]    // caption-only, no language tag
    [InlineData("Movie.synced.srt")]    // junk without a language tag
    public void Normalize_NoLanguageTag_ReturnsNull(string input)
    {
        Assert.Null(Normalize(input));
    }

    [Fact]
    public void Normalize_UnknownJunkSuffix_ReturnsNull()
    {
        // No language anywhere in the trailing tags — leave for the AI detector.
        Assert.Null(Normalize("Movie.readme.srt"));
    }

    [Fact]
    public void Normalize_InvertedCaptionedLanguage_CorrectsOrder()
    {
        // Non-standard caption-before-language variants (sdh/cc/forced).
        Assert.Equal("Movie.fr.sdh.srt", Normalize("Movie.sdh.fr.srt"));
        Assert.Equal("Movie.fr.cc.srt", Normalize("Movie.cc.fr.srt"));
    }
}
