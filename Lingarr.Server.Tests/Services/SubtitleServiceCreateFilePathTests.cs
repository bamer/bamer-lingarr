using Lingarr.Server.Services;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Tests for output file naming. The TARGET LANGUAGE must always be the final
/// filename segment: Jellyfin/Emby identify an external subtitle by the last
/// token, so "movie.th.hi.srt" showed up as Hindi while the Thai track the user
/// asked for looked "missing". Caption and optional tag go before the language.
/// </summary>
public class SubtitleServiceCreateFilePathTests
{
    private readonly SubtitleService _service = new(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<SubtitleService>.Instance,
        new LanguageCodeService());

    [Fact]
    public void CreateFilePath_PlainSource_AppendsLanguageLast()
    {
        var result = _service.CreateFilePath("/movies/m/Movie (2020).en.srt", "th", "");

        Assert.Equal("/movies/m/Movie (2020).th.srt", result);
    }

    [Fact]
    public void CreateFilePath_CaptionedSource_PutsCaptionBeforeLanguage()
    {
        // Regression: used to produce ".th.hi.srt" (language no longer last),
        // which Jellyfin reported as Hindi instead of Thai.
        var result = _service.CreateFilePath("/movies/m/Movie (2020).hi.srt", "th", "");

        Assert.Equal("/movies/m/Movie (2020).hi.th.srt", result);
    }

    [Fact]
    public void CreateFilePath_CaptionedSourceWithTag_KeepsLanguageLast()
    {
        var result = _service.CreateFilePath("/movies/m/Movie (2020).hi.srt", "th", "AI");

        Assert.Equal("/movies/m/Movie (2020).hi.ai.th.srt", result);
    }

    [Fact]
    public void CreateFilePath_PlainSourceWithTag_KeepsLanguageLast()
    {
        var result = _service.CreateFilePath("/movies/m/Movie (2020).en.srt", "th", "AI");

        Assert.Equal("/movies/m/Movie (2020).ai.th.srt", result);
    }

    [Fact]
    public void CreateFilePath_CounterSuffix_PreservesItAndKeepsLanguageLast()
    {
        var result = _service.CreateFilePath("/movies/m/Movie (2020).1.en.srt", "th", "");

        Assert.Equal("/movies/m/Movie (2020).1.th.srt", result);
    }

    [Fact]
    public void CreateFilePath_ForcedCaption_MovesCaptionBeforeLanguage()
    {
        var result = _service.CreateFilePath("/movies/m/Movie (2020).forced.en.srt", "fr", "");

        Assert.Equal("/movies/m/Movie (2020).forced.fr.srt", result);
    }

    [Fact]
    public void CreateFilePath_RemoveLanguageTarget_DropsLanguageSegment()
    {
        var result = _service.CreateFilePath("/movies/m/Movie (2020).en.srt", "", "");

        Assert.Equal("/movies/m/Movie (2020).srt", result);
    }
}
