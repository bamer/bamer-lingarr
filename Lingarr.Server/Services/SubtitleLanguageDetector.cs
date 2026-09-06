using System.Text.RegularExpressions;
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
            .Where(subtitle => string.IsNullOrEmpty(subtitle.Language) || subtitle.Language == "unknown")
            .ToList();
        if (unknown.Count == 0)
        {
            return false;
        }

        _logger.LogInformation(
            "Found {Count} subtitle file(s) without a language tag, attempting AI language detection.",
            unknown.Count);

        var service = await CreateDetectionServiceAsync();
        if (service == null)
        {
            _logger.LogWarning("Skipping language detection: no usable translation service is configured.");
            return false;
        }

        var renamedAny = false;
        foreach (var subtitle in unknown)
        {
            try
            {
                var code = await DetectFileLanguageAsync(service, subtitle.Path, cancellationToken);
                if (code == null)
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(subtitle.Path) ?? string.Empty;
                var baseName = Path.GetFileNameWithoutExtension(subtitle.Path);
                var extension = Path.GetExtension(subtitle.Path);
                var newPath = Path.Combine(directory, $"{baseName}.{code}{extension}");

                if (File.Exists(newPath))
                {
                    _logger.LogWarning(
                        "Language detection found '{Code}' for {File}, but {Target} already exists — leaving the file untouched.",
                        code, subtitle.Path, newPath);
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

        return renamedAny;
    }

    private async Task<ITranslationService?> CreateDetectionServiceAsync()
    {
        List<string> serviceNames;
        try
        {
            var raw = await _settingService.GetSetting(SettingKeys.Translation.ServiceType);
            serviceNames = TranslationServices.Parse(raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read translation service settings for language detection.");
            return null;
        }

        if (serviceNames.Count == 0)
        {
            return null;
        }

        try
        {
            return _translationServiceFactory.CreateTranslationService(serviceNames[0]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create translation service for language detection.");
            return null;
        }
    }

    private async Task<string?> DetectFileLanguageAsync(
        ITranslationService service,
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

        // Sample from the middle: headers/credits at the edges carry little signal.
        var texts = items
            .Skip(Math.Max(0, items.Count / 2 - SampleLineCount / 2))
            .SelectMany(item => item.PlaintextLines.Concat(item.Lines))
            .Select(line => line.Trim())
            .Where(line => line.Length > 1)
            .Take(SampleLineCount)
            .ToList();
        if (texts.Count < 3)
        {
            _logger.LogDebug("Not enough text in {File} for language detection.", filePath);
            return null;
        }

        var sample = string.Join("\n", texts);
        if (sample.Length > MaxSampleChars)
        {
            sample = sample[..MaxSampleChars];
        }

        string? reply;
        try
        {
            reply = await service.DetectLanguageAsync(sample, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Language detection request failed for {File}.", filePath);
            return null;
        }

        if (string.IsNullOrWhiteSpace(reply))
        {
            return null;
        }

        var token = LanguageCodeRegex().Match(reply.Trim().Trim('"', '\'', '`', '.', ')', '('));
        if (!token.Success)
        {
            _logger.LogWarning("Language detection returned an unparsable reply for {File}: {Reply}", filePath, reply);
            return null;
        }

        var normalized = token.Value.ToLowerInvariant().Replace('_', '-');
        try
        {
            normalized = LanguageCodeService.GetNormalizedCode(normalized);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning("Language detection returned an unknown code for {File}: {Reply}", filePath, reply);
            return null;
        }

        return _languageCodeService.Validate(normalized) ? normalized : null;
    }

    [GeneratedRegex("^[a-zA-Z]{2,3}([-_][a-zA-Z]{2,4})?$")]
    private static partial Regex LanguageCodeRegex();
}
