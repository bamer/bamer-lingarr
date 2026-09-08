using Lingarr.Server.Services;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Tests for output file naming. Media servers (Plex/Jellyfin/Emby) expect the
/// LANGUAGE to precede the caption: "Inception.2010.fr.srt" (regular),
/// "Inception.2010.fr.hi.srt" (HI/SDH). The optional custom tag goes before the
/// language so the standard tail is preserved.
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
    public void CreateFilePath_CaptionedSource_KeepsStandardLanguageBeforeCaption()
    {
        // Regression (2.27.0): ".hi.th.srt" broke Jellyfin — the standard is
        // "Movie.th.hi.srt" (language first, then the caption modifier).
        var result = _service.CreateFilePath("/movies/m/Movie (2020).hi.srt", "th", "");

        Assert.Equal("/movies/m/Movie (2020).th.hi.srt", result);
    }

    [Fact]
    public void CreateFilePath_CaptionedSourceWithTag_PutsTagBeforeLanguage()
    {
        var result = _service.CreateFilePath("/movies/m/Movie (2020).hi.srt", "th", "AI");

        Assert.Equal("/movies/m/Movie (2020).ai.th.hi.srt", result);
    }

    [Fact]
    public void CreateFilePath_PlainSourceWithTag_PutsTagBeforeLanguage()
    {
        var result = _service.CreateFilePath("/movies/m/Movie (2020).en.srt", "th", "AI");

        Assert.Equal("/movies/m/Movie (2020).ai.th.srt", result);
    }

    [Fact]
    public void CreateFilePath_CounterSuffix_PreservesItAndKeepsLanguageBeforeCaption()
    {
        var result = _service.CreateFilePath("/movies/m/Movie (2020).1.en.srt", "th", "");

        Assert.Equal("/movies/m/Movie (2020).1.th.srt", result);
    }

    [Fact]
    public void CreateFilePath_ForcedCaption_KeepsStandardLanguageBeforeCaption()
    {
        var result = _service.CreateFilePath("/movies/m/Movie (2020).forced.en.srt", "fr", "");

        Assert.Equal("/movies/m/Movie (2020).fr.forced.srt", result);
    }

    [Fact]
    public void CreateFilePath_RemoveLanguageTarget_DropsLanguageSegment()
    {
        var result = _service.CreateFilePath("/movies/m/Movie (2020).en.srt", "", "");

        Assert.Equal("/movies/m/Movie (2020).srt", result);
    }
}
