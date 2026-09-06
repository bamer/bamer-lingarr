using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Untagged subtitle files are identified via the AI service and renamed to
/// basename.code.ext so the automation can pick them up.
/// </summary>
public class SubtitleLanguageDetectorTests
{
    private static string WriteUntaggedSrt(string directory)
    {
        var builder = new StringBuilder();
        for (var i = 1; i <= 12; i++)
        {
            builder.AppendLine(i.ToString());
            builder.AppendLine($"00:00:{i:00},000 --> 00:00:{i + 1:00},000");
            builder.AppendLine($"Bonjour le monde numéro {i}");
            builder.AppendLine();
        }

        var path = Path.Combine(directory, "Some.Movie.2023.srt");
        File.WriteAllText(path, builder.ToString());
        return path;
    }

    private static SubtitleLanguageDetector CreateDetector(
        ITranslationService translationService,
        ISubtitleService? subtitleService = null)
    {
        var factoryMock = new Mock<ITranslationServiceFactory>();
        factoryMock
            .Setup(f => f.CreateTranslationService(It.IsAny<string>()))
            .Returns(translationService);

        var settingsMock = new Mock<ISettingService>();
        settingsMock
            .Setup(s => s.GetSetting(SettingKeys.Translation.ServiceType))
            .ReturnsAsync("localai");

        return new SubtitleLanguageDetector(
            factoryMock.Object,
            settingsMock.Object,
            new LanguageCodeService(),
            subtitleService ?? new SubtitleService(
                Mock.Of<ILogger<SubtitleService>>(),
                new LanguageCodeService()),
            Mock.Of<ILogger<SubtitleLanguageDetector>>());
    }

    [Fact]
    public async Task DetectAndRename_UnknownLanguage_RenamesFileWithDetectedCode()
    {
        using var tempDirectory = new TempDirectory();
        var path = WriteUntaggedSrt(tempDirectory.Path);

        var translationMock = new Mock<ITranslationService>();
        translationMock
            .Setup(s => s.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("fr");

        var detector = CreateDetector(translationMock.Object);
        var subtitles = new List<Subtitles>
        {
            new() { Path = path, FileName = "Some.Movie.2023", Language = "", Caption = "", Format = ".srt" }
        };

        var renamed = await detector.DetectAndRenameUnknownSubtitlesAsync(subtitles);

        Assert.True(renamed);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(tempDirectory.Path, "Some.Movie.2023.fr.srt")));
    }

    [Fact]
    public async Task DetectAndRename_UnsupportedService_LeavesFileUntouched()
    {
        using var tempDirectory = new TempDirectory();
        var path = WriteUntaggedSrt(tempDirectory.Path);

        // Default interface implementation reports "not supported" (null).
        var translationMock = new Mock<ITranslationService>(MockBehavior.Strict);

        var detector = CreateDetector(translationMock.Object);
        var subtitles = new List<Subtitles>
        {
            new() { Path = path, FileName = "Some.Movie.2023", Language = "", Caption = "", Format = ".srt" }
        };

        var renamed = await detector.DetectAndRenameUnknownSubtitlesAsync(subtitles);

        Assert.False(renamed);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DetectAndRename_TaggedFiles_AreIgnored()
    {
        using var tempDirectory = new TempDirectory();

        var translationMock = new Mock<ITranslationService>(MockBehavior.Strict);

        var detector = CreateDetector(translationMock.Object);
        var subtitles = new List<Subtitles>
        {
            new() { Path = "/tmp/x.en.srt", FileName = "x.en", Language = "en", Caption = "", Format = ".srt" }
        };

        var renamed = await detector.DetectAndRenameUnknownSubtitlesAsync(subtitles);

        Assert.False(renamed);
    }

    [Fact]
    public async Task DetectAndRename_SampleContainsDistinctLines()
    {
        using var tempDirectory = new TempDirectory();
        var path = WriteUntaggedSrt(tempDirectory.Path);

        string? capturedSample = null;
        var translationMock = new Mock<ITranslationService>();
        translationMock
            .Setup(s => s.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((sample, _) => capturedSample = sample)
            .ReturnsAsync("en");

        var detector = CreateDetector(translationMock.Object);
        var subtitles = new List<Subtitles>
        {
            new() { Path = path, FileName = "Some.Movie.2023", Language = "", Caption = "", Format = ".srt" }
        };

        await detector.DetectAndRenameUnknownSubtitlesAsync(subtitles);

        Assert.NotNull(capturedSample);
        var lines = capturedSample.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 1).ToList();
        Assert.Equal(10, lines.Count);
        Assert.Equal(lines.Count, lines.Distinct().Count());
    }

    [Fact]
    public async Task DetectAndRename_FirstServiceUnsupported_FallsBackToNext()
    {
        using var tempDirectory = new TempDirectory();
        var path = WriteUntaggedSrt(tempDirectory.Path);

        var unsupportedMock = new Mock<ITranslationService>();
        unsupportedMock
            .Setup(s => s.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var workingMock = new Mock<ITranslationService>();
        workingMock
            .Setup(s => s.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("fr");

        var factoryMock = new Mock<ITranslationServiceFactory>();
        factoryMock.Setup(f => f.CreateTranslationService("google")).Returns(unsupportedMock.Object);
        factoryMock.Setup(f => f.CreateTranslationService("localai")).Returns(workingMock.Object);

        var settingsMock = new Mock<ISettingService>();
        settingsMock
            .Setup(s => s.GetSetting(SettingKeys.Translation.ServiceType))
            .ReturnsAsync("[\"google\", \"localai\"]");

        var detector = new SubtitleLanguageDetector(
            factoryMock.Object,
            settingsMock.Object,
            new LanguageCodeService(),
            new SubtitleService(
                Mock.Of<ILogger<SubtitleService>>(),
                new LanguageCodeService()),
            Mock.Of<ILogger<SubtitleLanguageDetector>>());

        var subtitles = new List<Subtitles>
        {
            new() { Path = path, FileName = "Some.Movie.2023", Language = "", Caption = "", Format = ".srt" }
        };

        var renamed = await detector.DetectAndRenameUnknownSubtitlesAsync(subtitles);

        Assert.True(renamed);
        Assert.True(File.Exists(Path.Combine(tempDirectory.Path, "Some.Movie.2023.fr.srt")));
        workingMock.Verify(
            s => s.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
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
