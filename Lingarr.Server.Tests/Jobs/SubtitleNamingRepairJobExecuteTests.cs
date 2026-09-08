using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
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

/// <summary>
/// Collision behavior of SubtitleNamingRepairJob: when the standard-named file
/// already exists, a byte-identical legacy file is REMOVED (keeping it would
/// only accumulate on every pass) while a different file is renamed to a
/// standard name with a counter, and request paths are re-pointed either way.
/// </summary>
public class SubtitleNamingRepairJobExecuteTests : IDisposable
{
    private readonly LingarrDbContext _dbContext;
    private readonly TempDir _tempDir = new();

    public SubtitleNamingRepairJobExecuteTests()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new LingarrDbContext(options);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _tempDir.Dispose();
        GC.SuppressFinalize(this);
    }

    private SubtitleNamingRepairJob CreateJob()
    {
        var scheduleMock = new Mock<IScheduleService>();
        scheduleMock.Setup(s => s.UpdateJobState(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        return new SubtitleNamingRepairJob(
            _dbContext,
            scheduleMock.Object,
            new LanguageCodeService(),
            Mock.Of<ISettingService>(),
            NullLogger<SubtitleNamingRepairJob>.Instance);
    }

    private async Task SeedMovieAsync()
    {
        await _dbContext.Movies.AddAsync(new Movie
        {
            RadarrId = 1,
            Title = "Beur sur la ville (2011)",
            Path = _tempDir.Path,
            FileName = "Beur sur la ville (2011) - [SDTV]",
            IncludeInTranslation = true,
            DateAdded = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();
    }

    private string StandardPath() => Path.Combine(_tempDir.Path, "Beur sur la ville (2011) - [SDTV].en.hi.srt");
    private string LegacyPath() => Path.Combine(_tempDir.Path, "Beur sur la ville (2011) - [SDTV].hi.en.srt");

    [Fact]
    public async Task Execute_StandardFileAlreadyExistsWithIdenticalContent_RemovesLegacyDuplicate()
    {
        await SeedMovieAsync();
        var standard = StandardPath();
        var legacy = LegacyPath();
        File.WriteAllText(standard, "identical");
        File.WriteAllText(legacy, "identical");

        await CreateJob().Execute();

        Assert.True(File.Exists(standard), "standard file must survive");
        Assert.False(File.Exists(legacy), "byte-identical legacy duplicate must be removed");
    }

    [Fact]
    public async Task Execute_StandardFileAlreadyExists_DuplicateRemovalRePointsRecordedRequest()
    {
        await SeedMovieAsync();
        var standard = StandardPath();
        var legacy = LegacyPath();
        File.WriteAllText(standard, "identical");
        File.WriteAllText(legacy, "identical");

        // The broken name is what a 2.27 request recorded as its output path.
        await _dbContext.TranslationRequests.AddAsync(new TranslationRequest
        {
            MediaId = 1,
            Title = "Beur sur la ville (2011)",
            SourceLanguage = "fr",
            TargetLanguage = "en",
            SubtitleToTranslate = "/media/x/source.fr.srt",
            TranslatedSubtitle = legacy,
            MediaType = MediaType.Movie,
            Status = TranslationStatus.Completed
        });
        await _dbContext.SaveChangesAsync();

        await CreateJob().Execute();

        var request = (await _dbContext.TranslationRequests.ToListAsync()).Single();
        Assert.Equal(standard, request.TranslatedSubtitle);
    }

    [Fact]
    public async Task Execute_StandardFileAlreadyExistsWithDifferentContent_RenamesLegacyWithCounter()
    {
        await SeedMovieAsync();
        var standard = StandardPath();
        var legacy = LegacyPath();
        File.WriteAllText(standard, "standard content");
        File.WriteAllText(legacy, "different subtitle content");

        await CreateJob().Execute();

        Assert.True(File.Exists(standard));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(_tempDir.Path, "Beur sur la ville (2011) - [SDTV].2.en.hi.srt")),
            "different legacy file must be renamed to a standard name with a counter");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory().FullName;

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}