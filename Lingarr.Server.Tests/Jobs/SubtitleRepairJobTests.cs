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

    private static void WriteSrt(string path, int cueCount)
    {
        var builder = new StringBuilder();
        for (var i = 1; i <= cueCount; i++)
        {
            builder.AppendLine(i.ToString());
            builder.AppendLine($"00:00:{i:00},000 --> 00:00:{i + 1:00},000");
            builder.AppendLine($"Line {i}");
            builder.AppendLine();
        }
        File.WriteAllText(path, builder.ToString());
    }

    private async Task SeedMovieDirectoryAsync()
    {
        var movie = new Movie
        {
            RadarrId = 1,
            Title = "Repair Test",
            Path = _tempDirectory.Path,
            FileName = "movie",
            IncludeInTranslation = true,
            DateAdded = DateTime.UtcNow
        };
        await _dbContext.Movies.AddAsync(movie);
        await _dbContext.SaveChangesAsync();
    }

    private async Task<TranslationRequest> AddRequestAsync(string sourcePath, string translatedPath)
    {
        var request = new TranslationRequest
        {
            MediaId = 1,
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
    public async Task Execute_BrokenTargetWithDbLines_RebuildsFromDatabase()
    {
        var sourcePath = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        WriteSrt(sourcePath, 4);
        var brokenPath = Path.Combine(_tempDirectory.Path, "movie.th.srt");
        await File.WriteAllTextAsync(brokenPath, string.Empty);
        File.SetLastWriteTimeUtc(brokenPath, DateTime.UtcNow.AddHours(-1));

        await SeedMovieDirectoryAsync();
        var request = await AddRequestAsync(sourcePath, brokenPath);
        await _dbContext.TranslationRequestLines.AddRangeAsync(
            Enumerable.Range(1, 4).Select(i => new TranslationRequestLine
            {
                TranslationRequestId = request.Id,
                Position = i,
                Source = $"Line {i}",
                Target = $"Traduit {i}"
            }));
        await _dbContext.SaveChangesAsync();

        await CreateJob().Execute();

        var rebuilt = await File.ReadAllTextAsync(brokenPath);
        Assert.Contains("Traduit 1", rebuilt);
        Assert.Contains("Traduit 4", rebuilt);
    }

    [Fact]
    public async Task Execute_BrokenTargetWithoutUsableLines_DeletesCorpse()
    {
        var sourcePath = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        WriteSrt(sourcePath, 2);
        var brokenPath = Path.Combine(_tempDirectory.Path, "movie.th.srt");
        await File.WriteAllTextAsync(brokenPath, string.Empty);
        File.SetLastWriteTimeUtc(brokenPath, DateTime.UtcNow.AddHours(-1));

        await SeedMovieDirectoryAsync();
        var request = await AddRequestAsync(sourcePath, brokenPath);
        await _dbContext.TranslationRequestLines.AddAsync(new TranslationRequestLine
        {
            TranslationRequestId = request.Id,
            Position = 1,
            Source = "Line 1",
            Target = "  "
        });
        await _dbContext.SaveChangesAsync();

        await CreateJob().Execute();

        Assert.False(File.Exists(brokenPath));
    }

    [Fact]
    public async Task Execute_BrokenSource_DeletesSourceFile()
    {
        var sourcePath = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        await File.WriteAllTextAsync(sourcePath, string.Empty);
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddHours(-1));
        var translatedPath = Path.Combine(_tempDirectory.Path, "movie.th.srt");
        WriteSrt(translatedPath, 4);

        await SeedMovieDirectoryAsync();
        await AddRequestAsync(sourcePath, translatedPath);

        await CreateJob().Execute();

        Assert.False(File.Exists(sourcePath));
    }

    [Fact]
    public async Task Execute_HealthyTarget_LeftUntouched()
    {
        var sourcePath = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        WriteSrt(sourcePath, 4);
        var healthyPath = Path.Combine(_tempDirectory.Path, "movie.th.srt");
        WriteSrt(healthyPath, 4);
        File.SetLastWriteTimeUtc(healthyPath, DateTime.UtcNow.AddHours(-1));

        await SeedMovieDirectoryAsync();
        await AddRequestAsync(sourcePath, healthyPath);

        var before = File.GetLastWriteTimeUtc(healthyPath);
        await CreateJob().Execute();

        Assert.Equal(before, File.GetLastWriteTimeUtc(healthyPath));
    }

    [Fact]
    public async Task Execute_RecentlyWrittenBrokenFile_Skipped()
    {
        var sourcePath = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        WriteSrt(sourcePath, 2);
        var freshPath = Path.Combine(_tempDirectory.Path, "movie.th.srt");
        await File.WriteAllTextAsync(freshPath, string.Empty);

        await SeedMovieDirectoryAsync();
        await AddRequestAsync(sourcePath, freshPath);

        await CreateJob().Execute();

        Assert.True(File.Exists(freshPath));
    }

    [Fact]
    public async Task Execute_FileNotInDb_NotScanned()
    {
        var orphanPath = Path.Combine(_tempDirectory.Path, "orphan.fr.srt");
        await File.WriteAllTextAsync(orphanPath, string.Empty);
        File.SetLastWriteTimeUtc(orphanPath, DateTime.UtcNow.AddHours(-1));

        await SeedMovieDirectoryAsync();

        await CreateJob().Execute();

        Assert.True(File.Exists(orphanPath));
    }

    [Fact]
    public async Task Execute_ReverseDirection_RebuildsTarget()
    {
        var sourcePath = Path.Combine(_tempDirectory.Path, "movie.fr.srt");
        WriteSrt(sourcePath, 3);
        var brokenPath = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        await File.WriteAllTextAsync(brokenPath, string.Empty);
        File.SetLastWriteTimeUtc(brokenPath, DateTime.UtcNow.AddHours(-1));

        await SeedMovieDirectoryAsync();
        var request = await AddRequestAsync(sourcePath, brokenPath);
        await _dbContext.TranslationRequestLines.AddRangeAsync(
            Enumerable.Range(1, 3).Select(i => new TranslationRequestLine
            {
                TranslationRequestId = request.Id,
                Position = i,
                Source = $"Ligne {i}",
                Target = $"Translated line {i}"
            }));
        await _dbContext.SaveChangesAsync();

        await CreateJob().Execute();

        var rebuilt = await File.ReadAllTextAsync(brokenPath);
        Assert.Contains("Translated line 1", rebuilt);
        Assert.Contains("Translated line 3", rebuilt);
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
