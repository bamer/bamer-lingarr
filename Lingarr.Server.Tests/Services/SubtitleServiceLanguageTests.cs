using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Server.Models.FileSystem;
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
    public async Task GetAllSubtitles_StandardMediaNaming_DetectsLanguageAndCaption()
    {
        // Plex/Jellyfin standard: language then caption — "Inception.2010.fr.hi.srt".
        using var tempDirectory = new TempDirectory();
        var detected = await DetectAsync(CreateService(), tempDirectory.Path,
            "Inception.2010.REPACK.fr.srt",
            "Inception.2010.REPACK.fr.hi.srt",
            "Inception.2010.REPACK.fr.sdh.srt",
            "Movie.lingarr.fr.hi.srt",
            "Movie (2020).1.th.srt");

        Assert.Equal(("fr", ""), detected["Inception.2010.REPACK.fr.srt"]);
        Assert.Equal(("fr", "hi"), detected["Inception.2010.REPACK.fr.hi.srt"]);
        Assert.Equal(("fr", "sdh"), detected["Inception.2010.REPACK.fr.sdh.srt"]);
        // Custom tag before the language is ignored by the parser.
        Assert.Equal(("fr", "hi"), detected["Movie.lingarr.fr.hi.srt"]);
        // Multi-part counter suffix keeps the trailing language parseable.
        Assert.Equal(("th", ""), detected["Movie (2020).1.th.srt"]);
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
        // A lone ".hi" is ambiguous (Hearing Impaired vs Hindi): left untagged
        // with the caption kept, so the AI detector disambiguates it.
        Assert.Equal(("", "hi"), detected["test.movie.hi.srt"]);
    }

    [Fact]
    public async Task GetAllSubtitles_DoubledHiSuffix_ParsesAsHindiWithCaption()
    {
        using var tempDirectory = new TempDirectory();
        var detected = await DetectAsync(CreateService(), tempDirectory.Path,
            "Movie.hi.hi.srt");

        Assert.Equal(("hi", "hi"), detected["Movie.hi.hi.srt"]);
    }

    [Fact]
    public void SelectSourceSubtitle_RegionalSourceCode_MatchesNeutralFile()
    {
        // Domino case: settings hold en-US/fr-FR while files carry neutral en/fr tags.
        var service = CreateService();
        var subtitles = new List<Subtitles>
        {
            new() { Path = "/x/Domino.en.hi.srt", FileName = "Domino.en.hi", Language = "en", Caption = "hi", Format = ".srt" },
            new() { Path = "/x/Domino.fr.srt", FileName = "Domino.fr", Language = "fr", Caption = "", Format = ".srt" }
        };

        var selected = service.SelectSourceSubtitle(
            subtitles, new HashSet<string> { "en-US", "fr-FR" }, "true");

        Assert.NotNull(selected);
        Assert.Equal("en", selected.SourceLanguage);
    }

    [Fact]
    public void SelectSourceSubtitle_SourceCodeCase_DoesNotMatter()
    {
        var service = CreateService();
        var subtitles = new List<Subtitles>
        {
            new() { Path = "/x/Movie.en.srt", FileName = "Movie.en", Language = "en", Caption = "", Format = ".srt" }
        };

        var selected = service.SelectSourceSubtitle(
            subtitles, new HashSet<string> { "EN" }, "true");

        Assert.NotNull(selected);
        Assert.Equal("en", selected.SourceLanguage);
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
