using Lingarr.Contracts.Interfaces;
using Lingarr.Contracts.Models;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Services;

public class MediaSubtitleProcessor : IMediaSubtitleProcessor
{
    private readonly ITranslationRequestService _translationRequestService;
    private readonly ILogger<IMediaSubtitleProcessor> _logger;
    private readonly ISubtitleService _subtitleService;
    private readonly ISettingService _settingService;
    private readonly ISubtitleLanguageDetector _languageDetector;
    private readonly LanguageCodeService _languageCodeService;
    private readonly LingarrDbContext _dbContext;
    private IMedia _media = null!;
    private MediaType _mediaType;

    public MediaSubtitleProcessor(
        ITranslationRequestService translationRequestService,
        ILogger<IMediaSubtitleProcessor> logger,
        ISettingService settingService,
        ISubtitleService subtitleService,
        ISubtitleLanguageDetector languageDetector,
        LanguageCodeService languageCodeService,
        LingarrDbContext dbContext)
    {
        _translationRequestService = translationRequestService;
        _settingService = settingService;
        _subtitleService = subtitleService;
        _languageDetector = languageDetector;
        _languageCodeService = languageCodeService;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ProcessMedia(
        IMedia media,
        MediaType mediaType)
    {
        return await ProcessMediaWithOutcome(media, mediaType) == MediaProcessOutcome.Processed;
    }

    /// <inheritdoc />
    public async Task<MediaProcessOutcome> ProcessMediaWithOutcome(
        IMedia media, 
        MediaType mediaType)
    {
        if (media.Path == null || media.FileName == null)
        {
            _logger.LogWarning(
                "Skipping media processing: Path or FileName is null for {MediaType} with ID {MediaId}",
                mediaType, media.Id);
            return MediaProcessOutcome.SkippedInvalidMedia;
        }
        
        var subtitles = await _subtitleService.GetSubtitles(media.Path, media.FileName);
        if (!subtitles.Any())
        {
            _logger.LogDebug(
                "Skipping media {FileName}: no subtitle files matched.",
                media.FileName);
            return MediaProcessOutcome.SkippedNoSubtitles;
        }

        // Untagged files violate the naming convention and can never match a source
        // language — ask the AI to identify them and rename, then re-evaluate.
        // The detector itself no-ops when every file already carries a tag.
        var hadUntagged = subtitles.Any(subtitle =>
            string.IsNullOrEmpty(subtitle.Language) || subtitle.Language == "unknown");
        if (await _languageDetector.DetectAndRenameUnknownSubtitlesAsync(subtitles))
        {
            subtitles = await _subtitleService.GetSubtitles(media.Path, media.FileName);
            if (!subtitles.Any())
            {
                return MediaProcessOutcome.SkippedNoSubtitles;
            }
        }

        var sourceLanguages = await GetLanguagesSetting<SourceLanguage>(SettingKeys.Translation.SourceLanguages);
        var targetLanguages = await GetLanguagesSetting<TargetLanguage>(SettingKeys.Translation.TargetLanguages);
        var ignoreCaptions = await _settingService.GetSetting(SettingKeys.Translation.IgnoreCaptions) ?? "false";
        var captionSatisfiesTarget =
            await _settingService.GetSetting(SettingKeys.Translation.CaptionSatisfiesTarget) ?? "true";

        _media = media;
        _mediaType = mediaType;

        // ponytail: no hash shortcut — the file listing above is already done, so
        // re-verifying targets against actual files costs two small queries and can
        // never go stale (a Completed request for a deleted file re-queues).
        return await ProcessSubtitles(
            subtitles, sourceLanguages, targetLanguages, ignoreCaptions, captionSatisfiesTarget, hadUntagged);
    }

    /// <summary>
    /// Processes subtitle files for translation based on configured languages.
    /// </summary>
    /// <param name="subtitles">List of subtitle files to process.</param>
    /// <param name="sourceLanguages">The source languages.</param>
    /// <param name="targetLanguages">The target languages.</param>
    /// <param name="ignoreCaptions">The ignore captions setting.</param>
    /// <returns>The outcome: Processed when requests were created, otherwise the skip cause.</returns>
    private async Task<MediaProcessOutcome> ProcessSubtitles(
        List<Subtitles> subtitles,
        HashSet<string> sourceLanguages,
        HashSet<string> targetLanguages,
        string ignoreCaptions,
        string captionSatisfiesTarget,
        bool hadUntagged)
    {
        if (sourceLanguages.Count == 0 || targetLanguages.Count == 0)
        {
            _logger.LogWarning(
                "Source or target languages are empty. Source languages: {SourceCount}, Target languages: {TargetCount}",
                sourceLanguages.Count, targetLanguages.Count);
            return MediaProcessOutcome.SkippedNoSourceLanguage;
        }

        // A caption-only file (forced/SDH/HI) satisfies its target only when the
        // caption_satisfies_target setting is on. Either way it never blocks the
        // other targets (2548 medias were stuck on that before).
        // SelectSourceSubtitle already avoids picking captions as source when
        // ignoreCaptions is on.
        var selected = _subtitleService.SelectSourceSubtitle(subtitles, sourceLanguages, ignoreCaptions);
        if (selected == null || !targetLanguages.Any())
        {
            _logger.LogWarning(
                "No valid source language or target languages found for media |Green|{FileName}|/Green|. " +
                "Existing languages: |Red|{ExistingLanguages}|/Red|, " +
                "Source languages: |Red|{SourceLanguages}|/Red|, " +
                "Target languages: |Red|{TargetLanguages}|/Red|",
                string.Join(", ", _media?.FileName),
                string.Join(", ", subtitles.Select(s => s.Language.ToLowerInvariant())),
                string.Join(", ", sourceLanguages),
                string.Join(", ", targetLanguages));

            return hadUntagged
                ? MediaProcessOutcome.SkippedUnknownLanguage
                : MediaProcessOutcome.SkippedNoSourceLanguage;
        }

        // ponytail: same culture-aware matching as source selection — a regional
        // target (fr-FR) is satisfied by its neutral file (fr), not re-queued.
        var presentLanguages = captionSatisfiesTarget == "true"
            ? selected.AvailableLanguages
            : subtitles
                .Where(subtitle => string.IsNullOrEmpty(subtitle.Caption))
                .Select(subtitle => subtitle.Language.ToLowerInvariant())
                .ToHashSet();
        var languagesToTranslate = targetLanguages
            .Where(targetLanguage =>
                _languageCodeService.GetBestMatch(targetLanguage, presentLanguages) is null)
            .ToList();

        // ponytail: presence is verified against actual files above, so only work
        // that is still queued or running blocks a target. A Completed request
        // blocks only while its output file still exists — if the file was
        // deleted (or never written), the target must be re-queued (2548+ medias
        // were stuck this way; e.g. a completed Thai request with no .th file).
        var existingRequests = await _dbContext.TranslationRequests
            .Where(translationRequest => translationRequest.MediaId == _media.Id
                                         && translationRequest.MediaType == _mediaType
                                         && new[]
                                             {
                                                 TranslationStatus.Pending,
                                                 TranslationStatus.InProgress,
                                                 TranslationStatus.Completed
                                             }.Contains(translationRequest.Status))
            .Select(translationRequest => new
            {
                translationRequest.TargetLanguage,
                translationRequest.Status,
                translationRequest.TranslatedSubtitle
            })
            .ToListAsync();

        // Queued or running work always blocks its target language.
        var activeStatuses = new[]
        {
            TranslationStatus.Pending,
            TranslationStatus.InProgress
        };
        var activeTargets = existingRequests
            .Where(request => activeStatuses.Contains(request.Status))
            .Select(request => request.TargetLanguage)
            .ToHashSet();

        // A Completed request only satisfies its target when its output file is
        // still on disk; otherwise the translation must be redone.
        var completedTargets = existingRequests
            .Where(request => request.Status == TranslationStatus.Completed
                              && !string.IsNullOrEmpty(request.TranslatedSubtitle)
                              && File.Exists(request.TranslatedSubtitle))
            .Select(request => request.TargetLanguage)
            .ToHashSet();

        var blockedByRequests = languagesToTranslate
            .Where(targetLanguage => activeTargets.Contains(targetLanguage)
                                     || completedTargets.Contains(targetLanguage))
            .ToList();
        languagesToTranslate = languagesToTranslate
            .Except(activeTargets)
            .Except(completedTargets)
            .ToList();

        if (blockedByRequests.Any())
        {
            _logger.LogInformation(
                "Media |Green|{FileName}|/Green|: target(s) |Orange|{Languages}|/Orange| skipped — translation request(s) already exist ({Details}).",
                _media?.FileName,
                string.Join(", ", blockedByRequests),
                string.Join("; ", existingRequests
                    .Where(request => blockedByRequests.Contains(request.TargetLanguage))
                    .Select(request => $"{request.TargetLanguage}: {request.Status}")));
        }

        if (!languagesToTranslate.Any())
        {
            _logger.LogDebug(
                "Skipping media {FileName}: every target language is already present or requested.",
                _media?.FileName);
            return MediaProcessOutcome.SkippedNothingToTranslate;
        }

        foreach (var targetLanguage in languagesToTranslate)
        {
            await _translationRequestService.CreateRequest(new TranslateAbleSubtitle
            {
                MediaId = _media.Id,
                MediaType = _mediaType,
                SubtitlePath = selected.Subtitle.Path,
                TargetLanguage = targetLanguage,
                SourceLanguage = selected.SourceLanguage,
                SubtitleFormat = selected.Subtitle.Format
            });
            _logger.LogInformation(
                "Initiating translation from |Orange|{sourceLanguage}|/Orange| to |Orange|{targetLanguage}|/Orange| for |Green|{subtitleFile}|/Green|",
                selected.SourceLanguage,
                targetLanguage,
                selected.Subtitle.Path);
        }

        return MediaProcessOutcome.Processed;
    }

    /// <summary>
    /// Retrieves language settings from the application configuration.
    /// </summary>
    /// <typeparam name="T">The type of language setting to retrieve (Source or Target).</typeparam>
    /// <param name="settingName">The name of the setting to retrieve.</param>
    /// <returns>A HashSet of language codes from the configuration.</returns>
    private async Task<HashSet<string>> GetLanguagesSetting<T>(string settingName) where T : class, ILanguage
    {
        var languages = await _settingService.GetSettingAsJson<T>(settingName);
        return languages
            .Select(lang => lang.Code)
            .ToHashSet();
    }
}