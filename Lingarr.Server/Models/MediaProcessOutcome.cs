namespace Lingarr.Server.Models;

/// <summary>
/// Why a media item did or did not produce translation requests.
/// Used for end-of-run skip statistics.
/// </summary>
public enum MediaProcessOutcome
{
    /// <summary>New translation requests were created.</summary>
    Processed,
    /// <summary>Media path or file name is missing.</summary>
    SkippedInvalidMedia,
    /// <summary>No subtitle files matched the media file name.</summary>
    SkippedNoSubtitles,
    /// <summary>No subtitle file matches the configured source languages.</summary>
    SkippedNoSourceLanguage,
    /// <summary>Every target language is already present or requested.</summary>
    SkippedNothingToTranslate
}
