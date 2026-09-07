using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Jobs;
using Lingarr.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Jobs;

public class SubtitleRepairJobTests : IDisposable
{
    private readonly LingarrDbContext _dbContext;
    private readonly TempDirectory _tempDirectory;

    public SubtitleRepairJobTests()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new LingarrDbContext(options);
        _tempDirectory = new TempDirectory();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _tempDirectory.Dispose();
        GC.SuppressFinalize(this);
    }

    private SubtitleRepairJob CreateJob()
    {
        var scheduleMock = new Mock<IScheduleService>();
        scheduleMock
            .Setup(s => s.UpdateJobState(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        return new SubtitleRepairJob(
            _dbContext,
            scheduleMock.Object,
            new SubtitleService(
                Mock.Of<Microsoft.Extensions.Logging.ILogger<SubtitleService>>(),
                new LanguageCodeService()),
            NullLogger<SubtitleRepairJob>.Instance);
    }

    private static void WriteSource(string path)
    {
        var builder = new StringBuilder();
        for (var i = 1; i <= 4; i++)
        {
            builder.AppendLine(i.ToString());
            builder.AppendLine($"00:00:{i:00},000 --> 00:00:{i + 1:00},000");
            builder.AppendLine($"Source line {i}");
            builder.AppendLine();
        }
        File.WriteAllText(path, builder.ToString());
    }

    private async Task<Movie> AddMovieAsync(string fileName)
    {
        var movie = new Movie
        {
            RadarrId = 1,
            Title = "Repair Test",
            Path = _tempDirectory.Path,
            FileName = fileName,
            IncludeInTranslation = true,
            DateAdded = DateTime.UtcNow
        };
        await _dbContext.Movies.AddAsync(movie);
        await _dbContext.SaveChangesAsync();
        return movie;
    }

    private async Task<TranslationRequest> AddRequestAsync(
        int mediaId, string sourcePath, string translatedPath)
    {
        var request = new TranslationRequest
        {
            MediaId = mediaId,
            Title = "Repair Test",
            SourceLanguage = "en",
            TargetLanguage = "th",
            SubtitleToTranslate = sourcePath,
            TranslatedSubtitle = translatedPath,
            MediaType = MediaType.Movie,
            Status = TranslationStatus.Completed
        };
        await _dbContext.TranslationRequests.AddAsync(request);
        await _dbContext.SaveChangesAsync();
        return request;
    }

    [Fact]
    public async Task Execute_BrokenFileWithDbLines_RebuildsFromDatabase()
    {
        var sourcePath = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        WriteSource(sourcePath);
        var brokenPath = Path.Combine(_tempDirectory.Path, "movie.th.srt");
        await File.WriteAllTextAsync(brokenPath, string.Empty);
        File.SetLastWriteTimeUtc(brokenPath, DateTime.UtcNow.AddHours(-1));

        var movie = await AddMovieAsync("movie");
        var request = await AddRequestAsync(movie.Id, sourcePath, brokenPath);
        await _dbContext.TranslationRequestLines.AddRangeAsync(
            Enumerable.Range(1, 4).Select(i => new TranslationRequestLine
            {
                TranslationRequestId = request.Id,
                Position = i,
                Source = $"Source line {i}",
                Target = $"Ligne traduite {i}"
            }));
        await _dbContext.SaveChangesAsync();

        await CreateJob().Execute();

        var rebuilt = await File.ReadAllTextAsync(brokenPath);
        Assert.Contains("Ligne traduite 1", rebuilt);
        Assert.Contains("Ligne traduite 4", rebuilt);
    }

    [Fact]
    public async Task Execute_BrokenFileWithoutRequest_DeletesCorpse()
    {
        var brokenPath = Path.Combine(_tempDirectory.Path, "orphan.th.srt");
        await File.WriteAllTextAsync(brokenPath, string.Empty);
        File.SetLastWriteTimeUtc(brokenPath, DateTime.UtcNow.AddHours(-1));
        await AddMovieAsync("orphan");

        await CreateJob().Execute();

        Assert.False(File.Exists(brokenPath));
    }

    [Fact]
    public async Task Execute_BrokenFileWithOnlyBlankDbLines_DeletesCorpse()
    {
        var sourcePath = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        WriteSource(sourcePath);
        var brokenPath = Path.Combine(_tempDirectory.Path, "movie.th.srt");
        await File.WriteAllTextAsync(brokenPath, string.Empty);
        File.SetLastWriteTimeUtc(brokenPath, DateTime.UtcNow.AddHours(-1));

        var movie = await AddMovieAsync("movie");
        var request = await AddRequestAsync(movie.Id, sourcePath, brokenPath);
        await _dbContext.TranslationRequestLines.AddAsync(new TranslationRequestLine
        {
            TranslationRequestId = request.Id,
            Position = 1,
            Source = "Source line 1",
            Target = "  "
        });
        await _dbContext.SaveChangesAsync();

        await CreateJob().Execute();

        Assert.False(File.Exists(brokenPath));
    }

    [Fact]
    public async Task Execute_HealthyFile_LeftUntouched()
    {
        var sourcePath = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        WriteSource(sourcePath);
        await AddMovieAsync("movie");

        var before = File.GetLastWriteTimeUtc(sourcePath);
        await CreateJob().Execute();

        Assert.Equal(before, File.GetLastWriteTimeUtc(sourcePath));
    }

    [Fact]
    public async Task Execute_RecentlyWrittenBrokenFile_Skipped()
    {
        var brokenPath = Path.Combine(_tempDirectory.Path, "fresh.th.srt");
        await File.WriteAllTextAsync(brokenPath, string.Empty);
        await AddMovieAsync("fresh");

        await CreateJob().Execute();

        // Still there: may be mid-write by a running translation.
        Assert.True(File.Exists(brokenPath));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory().FullName;

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
