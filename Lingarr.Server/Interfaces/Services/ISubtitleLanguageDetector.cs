using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Interfaces.Services;

/// <summary>
/// Identifies the language of subtitle files that carry no language tag and
/// renames them to the standard naming convention (basename.code.ext).
/// </summary>
public interface ISubtitleLanguageDetector
{
    /// <summary>
    /// Detects the language of every untagged subtitle file and renames it.
    /// </summary>
    /// <param name="subtitles">Subtitle files of a single media item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when at least one file was renamed.</returns>
    Task<bool> DetectAndRenameUnknownSubtitlesAsync(
        List<Subtitles> subtitles,
        CancellationToken cancellationToken = default);
}
