using System;
using System.Threading.Tasks;
using Lingarr.Contracts.Models;
using Lingarr.Core.Configuration;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.MediaSubtitleProcessor;

/// <summary>
/// End-to-end tests for the request-blocking rules behind "missing translation
/// not detected": a media with en/fr sources and a missing th target must be
/// re-queued unless a live request (or a Completed request with its output file
/// still on disk) already covers the target.
/// </summary>
public class TargetBlockingTests : MediaSubtitleProcessorTestBase
{
    private async Task<Movie> SetupAirplaneIIScenario()
    {
        // en + fr sources on disk, no th file — the reported Airplane II case.
        var movie = await CreateTestMovie();
        var subtitles = new System.Collections.Generic.List<Subtitles>
        {
            new()
            {
                Path = $"/movies/test/{movie.FileName}.en.srt",
                FileName = $"{movie.FileName}.en",
                Language = "en",
                Caption = "",
                Format = ".srt"
            },
            new()
            {
                Path = $"/movies/test/{movie.FileName}.fr.srt",
                FileName = $"{movie.FileName}.fr",
                Language = "fr",
                Caption = "",
                Format = ".srt"
            }
        };

        SubtitleServiceMock
            .Setup(s => s.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(subtitles);
        SetupStandardSettings();
        SettingServiceMock
            .Setup(s => s.GetSettingAsJson<TargetLanguage>(SettingKeys.Translation.TargetLanguages))
            .ReturnsAsync(new System.Collections.Generic.List<TargetLanguage>
            {
                new() { Code = "th", Name = "Thai" }
            });
        return movie;
    }

    private static TranslationRequest NewRequest(
        int mediaId,
        TranslationStatus status,
        string? translatedSubtitle = null) => new()
    {
        MediaId = mediaId,
        Title = "Airplane II - The Sequel (1982)",
        SourceLanguage = "en",
        TargetLanguage = "th",
        SubtitleToTranslate = "/movies/test/test.movie.en.srt",
        TranslatedSubtitle = translatedSubtitle,
        MediaType = MediaType.Movie,
        Status = status
    };

    [Fact]
    public async Task ProcessMedia_MissingTargetWithoutRequest_IsQueued()
    {
        var movie = await SetupAirplaneIIScenario();

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.True(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t =>
                t.TargetLanguage == "th" && t.SourceLanguage == "en")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMedia_MissingTargetWithPendingRequest_IsNotReQueued()
    {
        var movie = await SetupAirplaneIIScenario();
        DbContext.TranslationRequests.Add(NewRequest(movie.Id, TranslationStatus.Pending));
        await DbContext.SaveChangesAsync();

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.False(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_MissingTargetWithInProgressRequest_IsNotReQueued()
    {
        var movie = await SetupAirplaneIIScenario();
        DbContext.TranslationRequests.Add(NewRequest(movie.Id, TranslationStatus.InProgress));
        await DbContext.SaveChangesAsync();

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.False(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_CompletedRequestWithDeletedOutputFile_IsReQueued()
    {
        var movie = await SetupAirplaneIIScenario();
        DbContext.TranslationRequests.Add(
            NewRequest(movie.Id, TranslationStatus.Completed, "/movies/test/never.written.th.srt"));
        await DbContext.SaveChangesAsync();

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.True(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t => t.TargetLanguage == "th")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMedia_CompletedRequestWithExistingOutputFile_IsNotReQueued()
    {
        var movie = await SetupAirplaneIIScenario();
        var existingOutput = System.IO.Path.Combine(
            System.IO.Directory.CreateTempSubdirectory().FullName,
            "test.movie.th.srt");
        await System.IO.File.WriteAllTextAsync(existingOutput, "stub");
        try
        {
            DbContext.TranslationRequests.Add(
                NewRequest(movie.Id, TranslationStatus.Completed, existingOutput));
            await DbContext.SaveChangesAsync();

            var result = await Processor.ProcessMedia(movie, MediaType.Movie);

            Assert.False(result);
            TranslationRequestServiceMock.Verify(
                s => s.CreateRequest(It.IsAny<TranslateAbleSubtitle>()),
                Times.Never);
        }
        finally
        {
            System.IO.File.Delete(existingOutput);
        }
    }

    [Fact]
    public async Task ProcessMedia_FailedRequest_DoesNotBlockTarget()
    {
        var movie = await SetupAirplaneIIScenario();
        DbContext.TranslationRequests.Add(NewRequest(movie.Id, TranslationStatus.Failed));
        await DbContext.SaveChangesAsync();

        var result = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.True(result);
        TranslationRequestServiceMock.Verify(
            s => s.CreateRequest(It.Is<TranslateAbleSubtitle>(t => t.TargetLanguage == "th")),
            Times.Once);
    }
}
