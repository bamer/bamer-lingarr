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
        scheduleMock.Setup(s => s.UpdateJobState(It.IsAny<string>(), It.IsAny<string>()))
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

    private async Task SeedMovieAsync()
    {
        await _dbContext.Movies.AddAsync(new Movie
        {
            RadarrId = 1, Title = "Test", Path = _tempDirectory.Path,
            FileName = "movie", IncludeInTranslation = true, DateAdded = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task<TranslationRequest> AddRequestAsync(string sourcePath, string translatedPath)
    {
        var request = new TranslationRequest
        {
            MediaId = 1, Title = "Test", SourceLanguage = "en", TargetLanguage = "th",
            SubtitleToTranslate = sourcePath, TranslatedSubtitle = translatedPath,
            MediaType = MediaType.Movie, Status = TranslationStatus.Completed
        };
        await _dbContext.TranslationRequests.AddAsync(request);
        await _dbContext.SaveChangesAsync();
        return request;
    }

    [Fact]
    public async Task Execute_BrokenTarget_RebuildsFromDb()
    {
        var src = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        var tgt = Path.Combine(_tempDirectory.Path, "movie.th.srt");
        WriteSrt(src, 4);
        await File.WriteAllTextAsync(tgt, string.Empty);
        File.SetLastWriteTimeUtc(tgt, DateTime.UtcNow.AddHours(-1));

        await SeedMovieAsync();
        var req = await AddRequestAsync(src, tgt);
        await _dbContext.TranslationRequestLines.AddRangeAsync(
            Enumerable.Range(1, 4).Select(i => new TranslationRequestLine
            {
                TranslationRequestId = req.Id, Position = i,
                Source = $"Line {i}", Target = $"Traduit {i}"
            }));
        await _dbContext.SaveChangesAsync();

        await CreateJob().Execute();

        var content = await File.ReadAllTextAsync(tgt);
        Assert.Contains("Traduit 1", content);
        Assert.Contains("Traduit 4", content);
    }

    [Fact]
    public async Task Execute_BrokenNoDb_DeletesCorpse()
    {
        var orphan = Path.Combine(_tempDirectory.Path, "orphan.th.srt");
        await File.WriteAllTextAsync(orphan, string.Empty);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-1));

        await SeedMovieAsync();
        await CreateJob().Execute();

        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task Execute_BrokenSource_DeletesSourceFile()
    {
        var src = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        var tgt = Path.Combine(_tempDirectory.Path, "movie.th.srt");
        await File.WriteAllTextAsync(src, string.Empty);
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddHours(-1));
        WriteSrt(tgt, 4);

        await SeedMovieAsync();
        await AddRequestAsync(src, tgt);

        await CreateJob().Execute();

        Assert.False(File.Exists(src));
    }

    [Fact]
    public async Task Execute_HealthyFile_NotTouched()
    {
        var src = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        var tgt = Path.Combine(_tempDirectory.Path, "movie.th.srt");
        WriteSrt(src, 4);
        WriteSrt(tgt, 4);
        File.SetLastWriteTimeUtc(tgt, DateTime.UtcNow.AddHours(-1));

        await SeedMovieAsync();
        await AddRequestAsync(src, tgt);

        var before = File.GetLastWriteTimeUtc(tgt);
        await CreateJob().Execute();
        Assert.Equal(before, File.GetLastWriteTimeUtc(tgt));
    }

    [Fact]
    public async Task Execute_RecentlyWritten_Skipped()
    {
        var src = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        var fresh = Path.Combine(_tempDirectory.Path, "movie.th.srt");
        WriteSrt(src, 2);
        await File.WriteAllTextAsync(fresh, string.Empty);

        await SeedMovieAsync();
        await AddRequestAsync(src, fresh);

        await CreateJob().Execute();
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task Execute_ReverseDirection_RebuildsTarget()
    {
        var src = Path.Combine(_tempDirectory.Path, "movie.fr.srt");
        var tgt = Path.Combine(_tempDirectory.Path, "movie.en.srt");
        WriteSrt(src, 3);
        await File.WriteAllTextAsync(tgt, string.Empty);
        File.SetLastWriteTimeUtc(tgt, DateTime.UtcNow.AddHours(-1));

        await SeedMovieAsync();
        var req = await AddRequestAsync(src, tgt);
        await _dbContext.TranslationRequestLines.AddRangeAsync(
            Enumerable.Range(1, 3).Select(i => new TranslationRequestLine
            {
                TranslationRequestId = req.Id, Position = i,
                Source = $"Ligne {i}", Target = $"Translated line {i}"
            }));
        await _dbContext.SaveChangesAsync();

        await CreateJob().Execute();

        var content = await File.ReadAllTextAsync(tgt);
        Assert.Contains("Translated line 1", content);
        Assert.Contains("Translated line 3", content);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory().FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}
