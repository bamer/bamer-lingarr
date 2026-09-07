using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lingarr.Contracts.Models;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.MediaSubtitleProcessor;

/// <summary>
/// Tests for the stale-entry pre-check: a media whose directory no longer exists
/// is skipped with a distinct outcome and no subtitle scan or request creation.
/// </summary>
public class MissingDirectoryTests : MediaSubtitleProcessorTestBase
{
    [Fact]
    public async Task ProcessMedia_WithMissingDirectory_ReturnsSkippedMissingDirectory()
    {
        // Arrange - movie path does not exist on disk
        var movie = await CreateTestMovie();
        movie.Path = "/definitely/not/a/real/path";
        await DbContext.SaveChangesAsync();

        SetupStandardSettings();

        // Act
        var outcome = await Processor.ProcessMediaWithOutcome(movie, MediaType.Movie);

        // Assert - distinct outcome, no subtitle scan, no request created
        Assert.Equal(MediaProcessOutcome.SkippedMissingDirectory, outcome);
        SubtitleServiceMock.Verify(
            s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_WithExistingDirectory_ProceedsNormally()
    {
        // Arrange - real temp directory from the test base
        var movie = await CreateTestMovie();
        var subtitles = new List<Subtitles>
        {
            new()
            {
                Path = Path.Combine(movie.Path!, "test.movie.en.srt"),
                FileName = "test.movie.en",
                Language = "en",
                Caption = "",
                Format = ".srt"
            }
        };

        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(subtitles);

        SetupStandardSettings();

        // Act
        var outcome = await Processor.ProcessMediaWithOutcome(movie, MediaType.Movie);

        // Assert - existing directory reaches the normal processing path
        Assert.Equal(MediaProcessOutcome.Processed, outcome);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Once);
    }
}