using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Server.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Language tags are only read from the trailing filename segments
/// (basename[.lang][.caption]). Words in the title itself must never match,
/// e.g. "So" in "You.Are.So.Beautiful" is not Somali.
/// </summary>
public class SubtitleServiceLanguageTests
{
    private static SubtitleService CreateService()
    {
        return new SubtitleService(
            Mock.Of<ILogger<SubtitleService>>(),
            new LanguageCodeService());
    }

    private static async Task<Dictionary<string, (string Language, string Caption)>> DetectAsync(
        SubtitleService service, string directory, params string[] fileNames)
    {
        foreach (var fileName in fileNames)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, fileName), "stub");
        }

        var subtitles = await service.GetAllSubtitles(directory);
        return subtitles.ToDictionary(
            subtitle => Path.GetFileName(subtitle.Path),
            subtitle => (subtitle.Language, subtitle.Caption));
    }

    [Fact]
    public async Task GetAllSubtitles_UntaggedTitleContainingLanguageCode_ReturnsEmptyLanguage()
    {
        using var tempDirectory = new TempDirectory();
        var detected = await DetectAsync(CreateService(), tempDirectory.Path,
            "You.Are.So.Beautiful.2023.srt",
            "It.2017.srt",
            "No.2012.srt");

        Assert.Equal("", detected["You.Are.So.Beautiful.2023.srt"].Language);
        Assert.Equal("", detected["It.2017.srt"].Language);
        Assert.Equal("", detected["No.2012.srt"].Language);
    }

    [Fact]
    public async Task GetAllSubtitles_TaggedFiles_DetectsLanguageAndCaption()
    {
        using var tempDirectory = new TempDirectory();
        var detected = await DetectAsync(CreateService(), tempDirectory.Path,
            "Movie.en.srt",
            "Movie.en.forced.srt",
            "Movie.forced.srt",
            "Movie.eng.srt",
            "test.movie.hi.srt");

        Assert.Equal(("en", ""), detected["Movie.en.srt"]);
        Assert.Equal(("en", "forced"), detected["Movie.en.forced.srt"]);
        Assert.Equal(("", "forced"), detected["Movie.forced.srt"]);
        Assert.Equal("en", detected["Movie.eng.srt"].Language);
        Assert.Equal(("hi", ""), detected["test.movie.hi.srt"]);
    }

    private sealed class TempDirectory : System.IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory().FullName;

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
