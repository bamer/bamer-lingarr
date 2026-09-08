using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services.Translation;

namespace Lingarr.Server.Services;

/// <summary>
/// Uses the configured translation service to identify the language of subtitle
/// files without a language tag, then renames them to basename.code.ext so the
/// automation can pick them up on the same pass.
/// </summary>
public partial class SubtitleLanguageDetector : ISubtitleLanguageDetector
{
    private const int SampleLineCount = 10;
    private const int MaxSampleChars = 1200;

    /// <summary>Caption tags recognized by Lingarr's filename parser.</summary>
    private static readonly HashSet<string> Captions = new(StringComparer.OrdinalIgnoreCase)
        { "sdh", "cc", "forced", "hi" };

    /// <summary>Junk suffixes to drop (VLC-style sync copies add ".synced").</summary>
    private static readonly HashSet<string> DropTokens = new(StringComparer.OrdinalIgnoreCase)
        { "synced", "sync" };

    // ponytail: AI failure memo — a file whose language could not be identified
    // used to be re-sent to the AI on every automation pass. Keyed by
    // path+size+mtime, so an edited file is a new version and gets retried.
    private static readonly ConcurrentDictionary<string, byte> DetectionFailures = new();

    private readonly ITranslationServiceFactory _translationServiceFactory;
    private readonly ISettingService _settingService;
    private readonly LanguageCodeService _languageCodeService;
    private readonly ISubtitleService _subtitleService;
    private readonly ILogger<SubtitleLanguageDetector> _logger;

    public SubtitleLanguageDetector(
        ITranslationServiceFactory translationServiceFactory,
        ISettingService settingService,
        LanguageCodeService languageCodeService,
        ISubtitleService subtitleService,
        ILogger<SubtitleLanguageDetector> logger)
    {
        _translationServiceFactory = translationServiceFactory;
        _settingService = settingService;
        _languageCodeService = languageCodeService;
        _subtitleService = subtitleService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> DetectAndRenameUnknownSubtitlesAsync(
        List<Subtitles> subtitles,
        CancellationToken cancellationToken = default)
    {
        var unknown = subtitles
            .Where(IsUntagged)
            .ToList();
        if (unknown.Count == 0)
        {
            return false;
        }

        var tagged = subtitles
            .Where(subtitle => !IsUntagged(subtitle))
            .ToList();

        // ponytail: exact duplicates of a tagged file carry zero information —
        // the same subtitle imported both tagged and untagged. Remove them right
        // away, no AI request needed, and the loop is over for good.
        var deletedAny = await DeleteTaggedDuplicatesAsync(unknown, tagged) > 0;
        var remaining = unknown
            .Where(subtitle => File.Exists(subtitle.Path))
            .ToList();
        if (remaining.Count == 0)
        {
            return deletedAny;
        }

        // ponytail: a tagged source-language file already exists → the untagged
        // leftovers are redundant for this media. Never spend AI requests on them
        // (the automation can already generate every missing target from the
        // tagged source).
        if (await TaggedSourceExistsAsync(tagged))
        {
            _logger.LogInformation(
                "Skipping AI language detection for {Count} untagged subtitle file(s) in {Directory}: a source-language file is already tagged, the untagged file(s) are redundant.",
                remaining.Count, Path.GetDirectoryName(remaining[0].Path));
            return deletedAny;
        }

        // ponytail: files whose detection already failed for this exact version
        // are not re-sent to the AI on every pass.
        var detectable = remaining
            .Where(subtitle => !DetectionFailures.ContainsKey(FailureKey(subtitle)))
            .ToList();
        if (detectable.Count == 0)
        {
            _logger.LogDebug(
                "Skipping AI language detection for {Count} untagged subtitle file(s): detection already failed for this file version.",
                remaining.Count);
            return deletedAny;
        }

        _logger.LogInformation(
            "Found {Count} subtitle file(s) without a language tag, attempting AI language detection.",
            detectable.Count);

        List<string> serviceNames;
        try
        {
            var raw = await _settingService.GetSetting(SettingKeys.Translation.ServiceType);
            serviceNames = TranslationServices.Parse(raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read translation service settings for language detection.");
            return deletedAny;
        }

        if (serviceNames.Count == 0)
        {
            _logger.LogWarning("Skipping language detection: no translation service is configured.");
            return deletedAny;
        }

        var renamedAny = false;
        foreach (var subtitle in detectable)
        {
            try
            {
                var code = await DetectFileLanguageAsync(serviceNames, subtitle.Path, cancellationToken);
                if (code == null)
                {
                    DetectionFailures[FailureKey(subtitle)] = 0;
                    continue;
                }

                var directory = Path.GetDirectoryName(subtitle.Path) ?? string.Empty;
                var baseName = Path.GetFileNameWithoutExtension(subtitle.Path);
                var extension = Path.GetExtension(subtitle.Path);
                var newPath = Path.Combine(directory, BuildCorrectedName(baseName, code, extension));

                if (File.Exists(newPath))
                {
                    if (await FilesAreEqualAsync(subtitle.Path, newPath))
                    {
                        // The tagged target has the same content: the untagged
                        // file is a duplicate — remove it instead of looping.
                        File.Delete(subtitle.Path);
                        deletedAny = true;
                        _logger.LogInformation(
                            "Detected language '{Code}' for {File}, but {Target} has identical content — removed the untagged duplicate.",
                            code, subtitle.Path, newPath);
                        continue;
                    }

                    // Different content: keep both. A counter suffix placed
                    // BEFORE the language keeps the trailing segment parseable
                    // (basename.2.en.srt → language 'en'), so the file stops
                    // being seen as untagged.
                    var counter = 2;
                    string suffixedPath;
                    do
                    {
                        suffixedPath = Path.Combine(directory, BuildCorrectedName(baseName, code, extension, counter));
                        counter++;
                    } while (File.Exists(suffixedPath));

                    File.Move(subtitle.Path, suffixedPath);
                    renamedAny = true;
                    _logger.LogInformation(
                        "Detected subtitle language '{Code}' for {File}; {Target} already exists with different content, renamed to {NewTarget}.",
                        code, subtitle.Path, newPath, suffixedPath);
                    continue;
                }

                File.Move(subtitle.Path, newPath);
                _logger.LogInformation(
                    "Detected subtitle language '{Code}' for {File}, renamed to {Target}.",
                    code, subtitle.Path, newPath);
                renamedAny = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Language detection failed for {File}.", subtitle.Path);
            }
        }

        return renamedAny || deletedAny;
    }

    private static bool IsUntagged(Subtitles subtitle) =>
        string.IsNullOrEmpty(subtitle.Language) || subtitle.Language == "unknown";

    /// <summary>
    /// Builds the media-server standard name for a detected file:
    /// "stem.{code}.srt" or "stem.{code}.{caption}.srt" (caption AFTER the
    /// language, per Plex/Jellyfin/Emby). Trailing junk ("synced") is dropped;
    /// a collision counter is inserted before the language so the trailing
    /// segments stay parseable.
    /// </summary>
    private static string BuildCorrectedName(string baseName, string code, string extension, int? counter = null)
    {
        var tokens = baseName.Split('.');
        var captions = new List<string>();
        var idx = tokens.Length - 1;
        while (idx >= 0)
        {
            var token = tokens[idx];
            if (Captions.Contains(token))
            {
                captions.Add(token); // collected in reverse order
                idx--;
                continue;
            }
            if (DropTokens.Contains(token))
            {
                idx--; // drop junk (synced/sync)
                continue;
            }
            break;
        }

        var parts = new List<string>();
        var stemEnd = idx + 1;
        parts.Add(string.Join('.', tokens[..stemEnd]));
        if (counter != null)
        {
            parts.Add(counter.ToString());
        }
        parts.Add(code);
        foreach (var caption in captions.AsEnumerable().Reverse().ToList())
        {
            parts.Add(caption);
        }
        return string.Join('.', parts) + extension;
    }

    private static string FailureKey(Subtitles subtitle)
    {
        var info = new FileInfo(subtitle.Path);
        return $"{subtitle.Path}|{(info.Exists ? info.Length : -1)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
    }

    private static async Task<string> ComputeHashAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private static async Task<bool> FilesAreEqualAsync(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        return await ComputeHashAsync(left) == await ComputeHashAsync(right);
    }

    /// <summary>
    /// Removes untagged files whose content is byte-identical to a tagged sibling.
    /// </summary>
    /// <returns>The number of files removed.</returns>
    private async Task<int> DeleteTaggedDuplicatesAsync(List<Subtitles> unknown, List<Subtitles> tagged)
    {
        if (tagged.Count == 0)
        {
            return 0;
        }

        var taggedHashes = new List<(string Path, string Hash)>();
        foreach (var taggedFile in tagged)
        {
            if (File.Exists(taggedFile.Path))
            {
                taggedHashes.Add((taggedFile.Path, await ComputeHashAsync(taggedFile.Path)));
            }
        }

        var deleted = 0;
        foreach (var untaggedFile in unknown)
        {
            if (!File.Exists(untaggedFile.Path))
            {
                continue;
            }

            var hash = await ComputeHashAsync(untaggedFile.Path);
            var twin = taggedHashes.FirstOrDefault(candidate => candidate.Hash == hash);
            if (twin.Path == null)
            {
                continue;
            }

            File.Delete(untaggedFile.Path);
            deleted++;
            _logger.LogInformation(
                "Removed untagged subtitle {File} — identical content to tagged {Twin}.",
                untaggedFile.Path, twin.Path);
        }

        return deleted;
    }

    /// <summary>
    /// True when a tagged subtitle file matches one of the configured source
    /// languages, i.e. the media already has a usable source.
    /// </summary>
    private async Task<bool> TaggedSourceExistsAsync(List<Subtitles> tagged)
    {
        if (tagged.Count == 0)
        {
            return false;
        }

        List<SourceLanguage> sourceLanguages;
        try
        {
            sourceLanguages = await _settingService.GetSettingAsJson<SourceLanguage>(SettingKeys.Translation.SourceLanguages) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read source languages for detection skip check.");
            return false;
        }

        var taggedLanguages = tagged
            .Select(subtitle => subtitle.Language.ToLowerInvariant())
            .ToHashSet();
        return sourceLanguages.Any(source =>
            _languageCodeService.GetBestMatch(source.Code, taggedLanguages) != null);
    }

    private async Task<string?> DetectFileLanguageAsync(
        List<string> serviceNames,
        string filePath,
        CancellationToken cancellationToken)
    {
        List<SubtitleItem> items;
        try
        {
            items = await _subtitleService.ReadSubtitles(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read {File} for language detection.", filePath);
            return null;
        }

        // Sample distinct lines from the middle: headers/credits at the edges carry
        // little signal, and PlaintextLines already equals Lines on untagged files —
        // concatenating both sent every line twice.
        var texts = items
            .Skip(Math.Max(0, items.Count / 2 - SampleLineCount / 2))
            .SelectMany(item => item.PlaintextLines.Count > 0 ? item.PlaintextLines : item.Lines)
            .Select(line => line.Trim())
            .Where(line => line.Length > 1)
            .Distinct()
            .Take(SampleLineCount)
            .ToList();
        if (texts.Count < 3)
        {
            _logger.LogWarning(
                "Not enough distinct text in {File} for language detection ({Count} lines).",
                filePath, texts.Count);
            return null;
        }

        var sample = string.Join("\n", texts);
        if (sample.Length > MaxSampleChars)
        {
            sample = sample[..MaxSampleChars];
        }

        // Try each configured service in order; the first usable code wins.
        string? lastReply = null;
        foreach (var serviceName in serviceNames)
        {
            ITranslationService service;
            try
            {
                service = _translationServiceFactory.CreateTranslationService(serviceName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not create translation service '{Name}' for language detection.", serviceName);
                continue;
            }

            string? reply;
            try
            {
                reply = await service.DetectLanguageAsync(sample, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Language detection request failed for {File} ({Service}).", filePath, serviceName);
                continue;
            }

            if (string.IsNullOrWhiteSpace(reply))
            {
                _logger.LogDebug("Service '{Service}' returned no language for {File}.", serviceName, filePath);
                continue;
            }

            lastReply = reply;
            var code = SanitizeLanguageCode(reply);
            if (code != null)
            {
                return code;
            }
        }

        _logger.LogWarning(
            "No configured translation service could identify the language of {File}. Last reply: {Reply}",
            filePath, lastReply ?? "<none>");
        return null;
    }

    private string? SanitizeLanguageCode(string reply)
    {
        var token = LanguageCodeRegex().Match(reply.Trim().Trim('"', '\'', '`', '.', ')', '('));
        if (!token.Success)
        {
            return null;
        }

        var normalized = token.Value.ToLowerInvariant().Replace('_', '-');
        try
        {
            normalized = LanguageCodeService.GetNormalizedCode(normalized);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return _languageCodeService.Validate(normalized) ? normalized : null;
    }

    [GeneratedRegex("^[a-zA-Z]{2,3}([-_][a-zA-Z]{2,4})?$")]
    private static partial Regex LanguageCodeRegex();
}
