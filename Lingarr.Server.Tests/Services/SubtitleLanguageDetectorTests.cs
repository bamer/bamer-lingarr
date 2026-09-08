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
    private static string WriteUntaggedSrt(string directory, string fileName = "Some.Movie.2023")
    {
        var builder = new StringBuilder();
        for (var i = 1; i <= 12; i++)
        {
            builder.AppendLine(i.ToString());
            builder.AppendLine($"00:00:{i:00},000 --> 00:00:{i + 1:00},000");
            builder.AppendLine($"Bonjour le monde numéro {i}");
            builder.AppendLine();
        }

        var path = Path.Combine(directory, $"{fileName}.srt");
        File.WriteAllText(path, builder.ToString());
        return path;
    }

    private static SubtitleLanguageDetector CreateDetector(
        ITranslationService translationService,
        ISubtitleService? subtitleService = null,
        string? sourceLanguageCode = null)
    {
        var factoryMock = new Mock<ITranslationServiceFactory>();
        factoryMock
            .Setup(f => f.CreateTranslationService(It.IsAny<string>()))
            .Returns(translationService);

        var settingsMock = new Mock<ISettingService>();
        settingsMock
            .Setup(s => s.GetSetting(SettingKeys.Translation.ServiceType))
            .ReturnsAsync("localai");
        settingsMock
            .Setup(s => s.GetSettingAsJson<Lingarr.Contracts.Models.SourceLanguage>(SettingKeys.Translation.SourceLanguages))
            .ReturnsAsync(sourceLanguageCode == null
                ? new List<Lingarr.Contracts.Models.SourceLanguage>()
                : new List<Lingarr.Contracts.Models.SourceLanguage> { new() { Code = sourceLanguageCode, Name = sourceLanguageCode } });

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
    public async Task DetectAndRename_UntaggedCaptionedFile_RenamesToStandardLanguageCaptionOrder()
    {
        // Movie.hi.srt (untagged caption) detected as French must become the
        // media-server standard "Movie.fr.hi.srt" — caption AFTER the language.
        using var tempDirectory = new TempDirectory();
        var path = WriteUntaggedSrt(tempDirectory.Path, "Some.Movie.2023.hi");

        var translationMock = new Mock<ITranslationService>();
        translationMock
            .Setup(s => s.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("fr");

        var detector = CreateDetector(translationMock.Object);
        var subtitles = new List<Subtitles>
        {
            new() { Path = path, FileName = "Some.Movie.2023.hi", Language = "", Caption = "hi", Format = ".srt" }
        };

        var renamed = await detector.DetectAndRenameUnknownSubtitlesAsync(subtitles);

        Assert.True(renamed);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(tempDirectory.Path, "Some.Movie.2023.fr.hi.srt")));
    }

    [Fact]
    public async Task DetectAndRename_UntaggedFileWithSyncedSuffix_DropsSyncedInStandardName()
    {
        // VLC-style ".synced" suffix is dropped: Some.Movie.2023.synced.srt
        // detected as French → "Some.Movie.2023.fr.srt".
        using var tempDirectory = new TempDirectory();
        var path = WriteUntaggedSrt(tempDirectory.Path, "Some.Movie.2023.synced");

        var translationMock = new Mock<ITranslationService>();
        translationMock
            .Setup(s => s.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("fr");

        var detector = CreateDetector(translationMock.Object);
        var subtitles = new List<Subtitles>
        {
            new() { Path = path, FileName = "Some.Movie.2023.synced", Language = "", Caption = "", Format = ".srt" }
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
    public async Task DetectAndRename_UntaggedDuplicateOfTaggedFile_RemovesWithoutAiRequest()
    {
        using var tempDirectory = new TempDirectory();
        var untaggedPath = WriteUntaggedSrt(tempDirectory.Path);
        var taggedPath = Path.Combine(tempDirectory.Path, "Some.Movie.2023.en.srt");
        File.Copy(untaggedPath, taggedPath);

        var translationMock = new Mock<ITranslationService>(MockBehavior.Strict);

        var detector = CreateDetector(translationMock.Object);
        var subtitles = new List<Subtitles>
        {
            new() { Path = untaggedPath, FileName = "Some.Movie.2023", Language = "", Caption = "", Format = ".srt" },
            new() { Path = taggedPath, FileName = "Some.Movie.2023.en", Language = "en", Caption = "", Format = ".srt" }
        };

        var renamed = await detector.DetectAndRenameUnknownSubtitlesAsync(subtitles);

        // A file was removed → true (the caller re-lists the directory).
        Assert.True(renamed);
        Assert.False(File.Exists(untaggedPath), "untagged duplicate should be removed");
        Assert.True(File.Exists(taggedPath), "tagged original must be kept");
    }

    [Fact]
    public async Task DetectAndRename_WhenTaggedSourceExists_SkipsAiDetection()
    {
        using var tempDirectory = new TempDirectory();
        var untaggedPath = WriteUntaggedSrt(tempDirectory.Path);
        var taggedPath = Path.Combine(tempDirectory.Path, "Some.Movie.2023.en.srt");
        // Different content: the untagged file is NOT a byte-identical duplicate,
        // so the only thing that avoids the AI is the tagged source language.
        File.WriteAllText(taggedPath, File.ReadAllText(untaggedPath).Replace("1", "99"));

        var translationMock = new Mock<ITranslationService>(MockBehavior.Strict);
        var detector = CreateDetector(translationMock.Object, sourceLanguageCode: "en");
        var subtitles = new List<Subtitles>
        {
            new() { Path = untaggedPath, FileName = "Some.Movie.2023", Language = "", Caption = "", Format = ".srt" },
            new() { Path = taggedPath, FileName = "Some.Movie.2023.en", Language = "en", Caption = "", Format = ".srt" }
        };

        var renamed = await detector.DetectAndRenameUnknownSubtitlesAsync(subtitles);

        Assert.False(renamed);
        Assert.True(File.Exists(untaggedPath), "untagged file left untouched by design");
        // No AI request was made.
    }

    [Fact]
    public async Task DetectAndRename_CollisionSameLanguageDifferentContent_renamesWithCounterSuffix()
    {
        using var tempDirectory = new TempDirectory();
        var untaggedPath = WriteUntaggedSrt(tempDirectory.Path);
        var taggedPath = Path.Combine(tempDirectory.Path, "Some.Movie.2023.en.srt");
        File.WriteAllText(taggedPath, File.ReadAllText(untaggedPath).Replace("1", "99"));

        var translationMock = new Mock<ITranslationService>();
        translationMock
            .Setup(s => s.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("en");

        var detector = CreateDetector(translationMock.Object);
        var subtitles = new List<Subtitles>
        {
            new() { Path = untaggedPath, FileName = "Some.Movie.2023", Language = "", Caption = "", Format = ".srt" },
            new() { Path = taggedPath, FileName = "Some.Movie.2023.en", Language = "en", Caption = "", Format = ".srt" }
        };

        var renamed = await detector.DetectAndRenameUnknownSubtitlesAsync(subtitles);

        Assert.True(renamed);
        Assert.False(File.Exists(untaggedPath), "untagged file should have been renamed");
        Assert.True(File.Exists(Path.Combine(tempDirectory.Path, "Some.Movie.2023.2.en.srt")),
            "different-content collision should produce a counter-suffixed tagged file");
    }

    [Fact]
    public async Task DetectAndRename_FailedDetectionOnce_DoesNotCallAiForSameVersionAgain()
    {
        using var tempDirectory = new TempDirectory();
        var path = WriteUntaggedSrt(tempDirectory.Path);
        var subtitles = new List<Subtitles>
        {
            new() { Path = path, FileName = "Some.Movie.2023", Language = "", Caption = "", Format = ".srt" }
        };

        var translationMock = new Mock<ITranslationService>();
        translationMock
            .Setup(s => s.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        // First call: detection fails and the version is memoized.
        var detector = CreateDetector(translationMock.Object);
        var first = await detector.DetectAndRenameUnknownSubtitlesAsync(subtitles);
        Assert.False(first);

        // Second call on the same file: the AI must not be contacted again.
        var second = await detector.DetectAndRenameUnknownSubtitlesAsync(subtitles);
        Assert.False(second);

        translationMock.Verify(
            s => s.DetectLanguageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
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
