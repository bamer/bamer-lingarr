using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.MediaSubtitleProcessor;

/// <summary>
/// Files without a language tag trigger AI detection + rename before the
/// normal source/target matching runs.
/// </summary>
public class LanguageDetectionHookTests : MediaSubtitleProcessorTestBase
{
    [Fact]
    public async Task ProcessMedia_UntaggedFile_RunsDetectionAndProceedsAfterRename()
    {
        var movie = await CreateTestMovie();
        var untagged = new Subtitles
        {
            Path = "/movies/test/test.movie.srt",
            FileName = "test.movie",
            Language = "",
            Caption = "",
            Format = ".srt"
        };
        var tagged = new Subtitles
        {
            Path = "/movies/test/test.movie.en.srt",
            FileName = "test.movie.en",
            Language = "en",
            Caption = "",
            Format = ".srt"
        };

        SubtitleServiceMock
            .SetupSequence(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles> { untagged })
            .ReturnsAsync(new List<Subtitles> { tagged });

        LanguageDetectorMock
            .Setup(d => d.DetectAndRenameUnknownSubtitlesAsync(
                It.IsAny<List<Subtitles>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        SetupStandardSettings(); // source=en, target=ro

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.True(result);
        LanguageDetectorMock.Verify(
            d => d.DetectAndRenameUnknownSubtitlesAsync(
                It.IsAny<List<Subtitles>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.SourceLanguage == "en" && t.TargetLanguage == "ro")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMedia_TaggedFiles_SkipsDetection()
    {
        var movie = await CreateTestMovie();
        var subtitles = new List<Subtitles>
        {
            new()
            {
                Path = "/movies/test/test.movie.en.srt",
                FileName = "test.movie.en",
                Language = "en",
                Caption = "",
                Format = ".srt"
            }
        };

        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(subtitles);

        SetupStandardSettings(); // source=en, target=ro

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.True(result);
        LanguageDetectorMock.Verify(
            d => d.DetectAndRenameUnknownSubtitlesAsync(
                It.IsAny<List<Subtitles>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
