using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Contracts.Exceptions;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Writing an empty subtitle file is never valid — it poisons downstream runs,
/// so WriteSubtitles must fail loudly instead of producing a 0-byte file.
/// </summary>
public class SubtitleServiceWriteTests
{
    private static SubtitleService CreateService()
    {
        return new SubtitleService(
            Mock.Of<ILogger<SubtitleService>>(),
            new LanguageCodeService());
    }

    private static SubtitleItem Item(int position, params string[] translated) => new()
    {
        Position = position,
        StartTime = position * 1000,
        EndTime = position * 1000 + 900,
        Lines = ["source"],
        PlaintextLines = ["source"],
        TranslatedLines = translated.ToList()
    };

    [Fact]
    public async Task WriteSubtitles_EmptyList_Throws()
    {
        using var tempDirectory = new TempDirectory();
        var path = Path.Combine(tempDirectory.Path, "out.srt");

        await Assert.ThrowsAsync<TranslationException>(() =>
            CreateService().WriteSubtitles(path, [], stripSubtitleFormatting: false));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WriteSubtitles_BlankTranslationsOnly_Throws()
    {
        using var tempDirectory = new TempDirectory();
        var path = Path.Combine(tempDirectory.Path, "out.srt");
        var subtitles = new List<SubtitleItem> { Item(1, ""), Item(2, "  ") };

        await Assert.ThrowsAsync<TranslationException>(() =>
            CreateService().WriteSubtitles(path, subtitles, stripSubtitleFormatting: false));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WriteSubtitles_WithContent_WritesFile()
    {
        using var tempDirectory = new TempDirectory();
        var path = Path.Combine(tempDirectory.Path, "out.srt");
        var subtitles = new List<SubtitleItem> { Item(1, "hello"), Item(2, "world") };

        await CreateService().WriteSubtitles(path, subtitles, stripSubtitleFormatting: false);

        Assert.True(new FileInfo(path).Length > 0);
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
