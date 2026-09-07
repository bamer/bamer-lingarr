using Hangfire;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Core.Exceptions;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Filters;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Integration;
using Lingarr.Server.Interfaces.Services.Sync;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.OpenApi.Extensions;

namespace Lingarr.Server.Jobs;

public class AutomatedTranslationJob
{
    private readonly LingarrDbContext _dbContext;
    private readonly ILogger<AutomatedTranslationJob> _logger;
    private readonly IMediaSubtitleProcessor _mediaSubtitleProcessor;
    private readonly ISettingService _settingService;
    private readonly IScheduleService _scheduleService;
    private readonly IMemoryCache _memoryCache;
    private readonly IRadarrService _radarrService;
    private readonly ISonarrService _sonarrService;
    private readonly IMovieSyncService _movieSyncService;
    private readonly IShowSyncService _showSyncService;
    private int _maxTranslationsPerRun = 10;
    private TimeSpan _defaultMovieAgeThreshold;
    private TimeSpan _defaultShowAgeThreshold;
    private int _staleRequestHours = 12;

    private const string MovieProcessingIndexKey = "Automation:MovieProcessingIndex";
    private const string ShowProcessingIndexKey = "Automation:ShowProcessingIndex";

    // ponytail: self-healing cap — a whole missing mount would otherwise fire one
    // Radarr/Sonarr API resync per media. Beyond the cap the entries are only
    // counted; the next scheduled sync (or a fixed mount) resolves the rest.
    private const int MaxResyncsPerRun = 25;

    public AutomatedTranslationJob(
        LingarrDbContext dbContext,
        ILogger<AutomatedTranslationJob> logger,
        IMediaSubtitleProcessor mediaSubtitleProcessor,
        IScheduleService scheduleService,
        ISettingService settingService,
        IMemoryCache memoryCache,
        IRadarrService radarrService,
        ISonarrService sonarrService,
        IMovieSyncService movieSyncService,
        IShowSyncService showSyncService)
    {
        _dbContext = dbContext;
        _logger = logger;
        _settingService = settingService;
        _scheduleService = scheduleService;
        _mediaSubtitleProcessor = mediaSubtitleProcessor;
        _memoryCache = memoryCache;
        _radarrService = radarrService;
        _sonarrService = sonarrService;
        _movieSyncService = movieSyncService;
        _showSyncService = showSyncService;
    }

    // ponytail: local gate instead of [DisableConcurrentExecution] — single-instance
    // deploy; Hangfire's distributed lock survives an ungraceful restart as a zombie
    // and blocked new runs for the full 10-min timeout ("Slow log ... OnPerforming")
    private static readonly SemaphoreSlim AutomationGate = new(1, 1);

    private const string AutomationRunStartedKey = "Automation:RunStartedAt";

    [AutomaticRetry(Attempts = 0)]
    [Queue("translation")]
    public async Task Execute()
    {
        if (!await AutomationGate.WaitAsync(TimeSpan.Zero))
        {
            // ponytail: warn (not info) — a gate held for hours means a previous
            // run is stuck (e.g. a blocked network share) and silently ate every
            // trigger since.
            var startedAt = _memoryCache.TryGetValue<DateTimeOffset>(AutomationRunStartedKey, out var since)
                ? since
                : DateTimeOffset.UtcNow;
            _logger.LogWarning(
                "Automation already running for {RunningFor}, skipping this trigger.",
                DateTimeOffset.UtcNow - startedAt);
            return;
        }

        _memoryCache.Set(
            AutomationRunStartedKey,
            DateTimeOffset.UtcNow,
            new MemoryCacheEntryOptions { Priority = CacheItemPriority.NeverRemove });
        try
        {
            await RunAutomation();
        }
        catch (Exception ex)
        {
            // ponytail: without this, a crash mid-pass leaves the job state stuck
            // on "Processing" forever and no summary is logged — the run just
            // "stops without reason".
            _logger.LogError(ex, "Automation run failed: {Message}", ex.Message);
            try
            {
                var failedJobName = JobContextFilter.GetCurrentJobTypeName();
                await _scheduleService.UpdateJobState(failedJobName, JobStatus.Failed.GetDisplayName());
            }
            catch (Exception stateEx)
            {
                _logger.LogError(stateEx, "Failed to update job state after automation error.");
            }

            throw;
        }
        finally
        {
            AutomationGate.Release();
            _memoryCache.Remove(AutomationRunStartedKey);
        }
    }

    private async Task RunAutomation()
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

        var settings = await _settingService.GetSettings([
            SettingKeys.Automation.AutomationEnabled,
            SettingKeys.Automation.TranslationCycle,
            SettingKeys.Automation.MaxTranslationsPerRun,
            SettingKeys.Automation.MovieAgeThreshold,
            SettingKeys.Automation.ShowAgeThreshold
        ]);

        if (settings[SettingKeys.Automation.AutomationEnabled] == "false")
        {
            _logger.LogInformation("Automation not enabled, skipping translation automation.");
            return;
        }

        int.TryParse(settings[SettingKeys.Automation.MaxTranslationsPerRun], out int maxTranslations);
        int.TryParse(settings[SettingKeys.Automation.MovieAgeThreshold], out int movieAgeThreshold);
        int.TryParse(settings[SettingKeys.Automation.ShowAgeThreshold], out int showAgeThreshold);
        int.TryParse(
            (await _settingService.GetSetting(SettingKeys.Automation.StaleRequestHours))
                ?? "12",
            out int staleRequestHours);

        _maxTranslationsPerRun = maxTranslations;
        _defaultMovieAgeThreshold = TimeSpan.FromHours(movieAgeThreshold);
        _defaultShowAgeThreshold = TimeSpan.FromHours(showAgeThreshold);
        _staleRequestHours = staleRequestHours <= 0 ? 12 : staleRequestHours;

        // ponytail: a request stuck in Pending/InProgress permanently blocks its
        // target language (the processor trusts that state). Stale ones — created
        // long ago, whose Hangfire job is gone (crash, manual purge, lost worker)
        // — are released as Interrupted so their targets can be re-queued. This is
        // what silently ate candidates like a missing Thai translation for Harlock.
        await ReconcileStaleRequests();

        var translationCycle = settings[SettingKeys.Automation.TranslationCycle] == "true" ? "movies" : "shows";
        _logger.LogInformation($"Starting translation cycle for |Green|{translationCycle}|/Green|");

        // ponytail: one grand-total summary at the end of the task — per-pass lines
        // get buried mid-logs on big libraries. Movies and episodes are tracked
        // separately so the summary shows what each side of the library
        // contributed (a 1622 total that is only movies is instantly visible).
        var total = new AutomationPassStats(0, 0, 0, 0, 0, 0, 0, 0);
        var movieStats = new AutomationPassStats(0, 0, 0, 0, 0, 0, 0, 0);
        var showStats = new AutomationPassStats(0, 0, 0, 0, 0, 0, 0, 0);
        switch (translationCycle)
        {
            case "movies":
                await _settingService.SetSetting(SettingKeys.Automation.TranslationCycle, "false");
                movieStats = await ProcessMovies(_maxTranslationsPerRun);
                total += movieStats;
                if (total.New < _maxTranslationsPerRun)
                {
                    showStats = await ProcessShows(_maxTranslationsPerRun - total.New);
                    total += showStats;
                }

                break;
            case "shows":
                await _settingService.SetSetting(SettingKeys.Automation.TranslationCycle, "true");
                showStats = await ProcessShows(_maxTranslationsPerRun);
                total += showStats;
                if (total.New < _maxTranslationsPerRun)
                {
                    movieStats = await ProcessMovies(_maxTranslationsPerRun - total.New);
                    total += movieStats;
                }

                break;
        }

        _logger.LogInformation(
            "Automation run complete: {New} new translations, {Scanned}/{Total} scanned " +
            "({MovieItems} movies + {EpisodeItems} episodes), {Skipped} skipped " +
            "(no subtitles: {NoSubs}, no source language: {NoSource}, unknown language: {Unknown}, already up to date: {UpToDate}, too recent: {TooRecent}, missing directory: {MissingPath}), " +
            "{Excluded} excluded by the IncludeInTranslation flag (not part of the scanned/total figures).",
            total.New,
            total.Scanned,
            total.Total,
            movieStats.Total,
            showStats.Total,
            total.NoSubtitles + total.NoSource + total.Unknown + total.UpToDate + total.TooRecent + total.MissingPath,
            total.NoSubtitles, total.NoSource, total.Unknown, total.UpToDate, total.TooRecent, total.MissingPath,
            total.Excluded);

        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
    }

    private bool ShouldProcessMedia(IMedia media, MediaType mediaType, TimeSpan? customAgeThreshold = null)
    {
        if (media.Path == null)
        {
            return false;
        }

        var fileInfo = new FileInfo(media.Path);
        var fileAge = TimeSpan.Zero;
        if (!fileInfo.Exists)
        {
            if (!media.DateAdded.HasValue)
            {
                return false;
            }

            fileAge = DateTime.UtcNow - media.DateAdded.Value.ToUniversalTime();
        }
        else
        {
            fileAge = DateTime.UtcNow - fileInfo.LastWriteTimeUtc;
        }

        var threshold = customAgeThreshold ??
                        (mediaType == MediaType.Movie ? _defaultMovieAgeThreshold : _defaultShowAgeThreshold);

        var fileAgeHours = fileAge.TotalHours;
        var thresholdHours = threshold.TotalHours;
        if (fileAgeHours >= thresholdHours)
        {
            return true;
        }

        _logger.LogInformation(
            "Media {FileName} does not meet age threshold. Age: {Age} hours, Required: {Threshold} hours",
            media.FileName,
            fileAgeHours.ToString("F2"),
            thresholdHours.ToString("F2"));
        return false;
    }

    private async Task<AutomationPassStats> ProcessMovies(int limit)
    {
        _logger.LogInformation("Movie Translation job initiated");

        var movies = await _dbContext.Movies
            .Where(movie => movie.IncludeInTranslation)
            .OrderBy(movie => movie.Id)
            .ToListAsync();

        // ponytail: flag-off rows explain "missing" items (scanned < library size).
        var excludedMovies = await _dbContext.Movies.CountAsync(movie => !movie.IncludeInTranslation);
        if (excludedMovies > 0)
        {
            _logger.LogInformation(
                "{Count} movies excluded by the IncludeInTranslation flag.",
                excludedMovies);
        }

        if (!movies.Any())
        {
            _logger.LogInformation("No translatable movies found.");
            return new AutomationPassStats(0, 0, 0, 0, 0, 0, 0, 0, excludedMovies);
        }

        // Instead of a random selection based on updatedAt, we will use a cycle so that all shows are processed.
        // Hopefully, this will prevent some shows from not being processed at all.
        var currentIndex = GetProcessingIndex(MovieProcessingIndexKey);
        if (currentIndex >= movies.Count)
        {
            currentIndex = 0;
            _logger.LogInformation("Movie processing cycle completed. Starting new cycle from the beginning.");
        }
        // Scan movies starting from `currentIndex` until we reach the maximum
        // number of translations or we processed all movies once; this ensures
        // we evaluate past the initial window when few items are eligible.
        _logger.LogInformation(
            "Processing up to {MaxTranslations} movies starting at {StartIndex} out of {TotalCount}",
            limit,
            currentIndex,
            movies.Count);

        var translationsInitiated = 0;
        var scannedMovies = 0;
        var index = currentIndex;
        var skippedNoSubtitles = 0;
        var skippedNoSource = 0;
        var skippedUnknown = 0;
        var skippedUpToDate = 0;
        var skippedTooRecent = 0;
        var skippedMissingPath = 0;
        var moviesWithMissingPath = new List<Movie>();

        while (translationsInitiated < limit && scannedMovies < movies.Count)
        {
            var movie = movies[index % movies.Count];
            try
            {
                TimeSpan? threshold = movie.TranslationAgeThreshold.HasValue
                    ? TimeSpan.FromHours(movie.TranslationAgeThreshold.Value)
                    : null;

                if (!ShouldProcessMedia(movie, MediaType.Movie, threshold))
                {
                    skippedTooRecent++;
                    continue;
                }

                var outcome = await _mediaSubtitleProcessor.ProcessMediaWithOutcome(movie, MediaType.Movie);
                switch (outcome)
                {
                    case MediaProcessOutcome.Processed:
                        translationsInitiated++;
                        break;
                    case MediaProcessOutcome.SkippedNoSubtitles:
                        skippedNoSubtitles++;
                        break;
                    case MediaProcessOutcome.SkippedMissingDirectory:
                        skippedMissingPath++;
                        moviesWithMissingPath.Add(movie);
                        break;
                    case MediaProcessOutcome.SkippedUnknownLanguage:
                        skippedUnknown++;
                        break;
                    case MediaProcessOutcome.SkippedNoSourceLanguage:
                    case MediaProcessOutcome.SkippedInvalidMedia:
                        skippedNoSource++;
                        break;
                    default:
                        skippedUpToDate++;
                        break;
                }
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found at path: |Red|{Path}|/Red|, skipping subtitle", movie.Path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error processing subtitles for movie at path: |Red|{Path}|/Red|, skipping subtitle",
                    movie.Path);
            }
            finally
            {
                index++;
                scannedMovies++;
            }
        }

        // ponytail: self-heal stale entries — resync the movie against Radarr so a
        // moved/renamed path is refreshed; a movie gone from Radarr loses its row.
        await ResyncMoviesWithMissingPaths(moviesWithMissingPath);

        var newIndex = index % movies.Count;
        SetProcessingIndex(MovieProcessingIndexKey, newIndex);

        _logger.LogInformation(
            "Movies pass complete: {New} new translations, {Scanned}/{Total} scanned, {Skipped} skipped " +
            "(no subtitles: {NoSubs}, no source language: {NoSource}, unknown language: {Unknown}, already up to date: {UpToDate}, too recent: {TooRecent}, missing directory: {MissingPath}).",
            translationsInitiated,
            scannedMovies,
            movies.Count,
            skippedNoSubtitles + skippedNoSource + skippedUnknown + skippedUpToDate + skippedTooRecent + skippedMissingPath,
            skippedNoSubtitles, skippedNoSource, skippedUnknown, skippedUpToDate, skippedTooRecent, skippedMissingPath);

        return new AutomationPassStats(
            translationsInitiated,
            scannedMovies,
            movies.Count,
            skippedNoSubtitles,
            skippedNoSource,
            skippedUnknown,
            skippedUpToDate,
            skippedTooRecent,
            excludedMovies,
            skippedMissingPath);
    }

    private async Task<AutomationPassStats> ProcessShows(int limit)
    {
        _logger.LogInformation("Show Translation job initiated");

        var shows = await _dbContext.Shows
            .Where(show => show.IncludeInTranslation)
            .ToListAsync();

        var seasons = await _dbContext.Seasons
            .Where(season => shows.Select(s => s.Id).Contains(season.ShowId) && season.IncludeInTranslation)
            .ToListAsync();

        var episodes = await _dbContext.Episodes
            .Where(episode =>
                seasons.Select(s => s.Id).Contains(episode.SeasonId) && episode.IncludeInTranslation)
            .OrderBy(e => e.Id)
            .ToListAsync();

        // ponytail: flag-off rows explain "missing" items (scanned < library size).
        var excludedShows = await _dbContext.Shows.CountAsync(show => !show.IncludeInTranslation);
        var excludedSeasons = await _dbContext.Seasons.CountAsync(season => !season.IncludeInTranslation);
        var excludedEpisodes = await _dbContext.Episodes.CountAsync(episode => !episode.IncludeInTranslation);
        var excludedCount = excludedShows + excludedSeasons + excludedEpisodes;
        if (excludedCount > 0)
        {
            _logger.LogInformation(
                "Excluded by the IncludeInTranslation flag: {Shows} shows, {Seasons} seasons, {Episodes} episodes.",
                excludedShows, excludedSeasons, excludedEpisodes);
        }

        if (!episodes.Any())
        {
            _logger.LogInformation("No translatable shows found.");
            return new AutomationPassStats(0, 0, 0, 0, 0, 0, 0, 0, excludedCount);
        }

        // Instead of a random selection based on updatedAt, we will use a cycle so that all shows are processed.
        // Hopefully, this will prevent some shows from not being processed at all.
        var currentIndex = GetProcessingIndex(ShowProcessingIndexKey);
        if (currentIndex >= episodes.Count)
        {
            currentIndex = 0;
            _logger.LogInformation("Show processing cycle completed. Starting new cycle from the beginning.");
        }
        _logger.LogInformation(
            "Processing up to {MaxTranslations} episodes starting at {StartIndex} out of {TotalCount}",
            limit,
            currentIndex,
            episodes.Count);

        var translationsInitiated = 0;
        var scannedEpisodes = 0;
        var episodeIndex = currentIndex;
        var skippedNoSubtitles = 0;
        var skippedNoSource = 0;
        var skippedUnknown = 0;
        var skippedUpToDate = 0;
        var skippedTooRecent = 0;
        var skippedMissingPath = 0;
        var showsWithMissingPath = new List<Show>();

        while (translationsInitiated < limit && scannedEpisodes < episodes.Count)
        {
            var episode = episodes[episodeIndex % episodes.Count];

            try
            {
                var season = seasons.FirstOrDefault(s => s.Id == episode.SeasonId);
                var show = shows.FirstOrDefault(s => s.Id == season?.ShowId);

                TimeSpan? threshold = null;
                if (show != null && show.TranslationAgeThreshold.HasValue)
                {
                    threshold = TimeSpan.FromHours(show.TranslationAgeThreshold.Value);
                }

                if (!ShouldProcessMedia(episode, MediaType.Episode, threshold))
                {
                    skippedTooRecent++;
                    continue;
                }

                var outcome = await _mediaSubtitleProcessor.ProcessMediaWithOutcome(episode, MediaType.Episode);
                switch (outcome)
                {
                    case MediaProcessOutcome.Processed:
                        translationsInitiated++;
                        break;
                    case MediaProcessOutcome.SkippedNoSubtitles:
                        skippedNoSubtitles++;
                        break;
                    case MediaProcessOutcome.SkippedMissingDirectory:
                        skippedMissingPath++;
                        if (show != null)
                        {
                            showsWithMissingPath.Add(show);
                        }

                        break;
                    case MediaProcessOutcome.SkippedUnknownLanguage:
                        skippedUnknown++;
                        break;
                    case MediaProcessOutcome.SkippedNoSourceLanguage:
                    case MediaProcessOutcome.SkippedInvalidMedia:
                        skippedNoSource++;
                        break;
                    default:
                        skippedUpToDate++;
                        break;
                }
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found for show at path: |Red|{Path}|/Red|, skipping episode",
                    episode.Path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error processing subtitles for episode at path: |Red|{Path}|/Red|, skipping episode",
                    episode.Path);
            }
            finally
            {
                episodeIndex++;
                scannedEpisodes++;
            }
        }

        // ponytail: self-heal stale entries — resync the whole show against Sonarr
        // so moved/renamed episode paths are refreshed; episodes removed in Sonarr
        // are dropped, and a show gone from Sonarr loses its row entirely.
        await ResyncShowsWithMissingPaths(showsWithMissingPath);

        var newIndex = episodeIndex % episodes.Count;
        SetProcessingIndex(ShowProcessingIndexKey, newIndex);

        _logger.LogInformation(
            "Episodes pass complete: {New} new translations, {Scanned}/{Total} scanned, {Skipped} skipped " +
            "(no subtitles: {NoSubs}, no source language: {NoSource}, unknown language: {Unknown}, already up to date: {UpToDate}, too recent: {TooRecent}, missing directory: {MissingPath}).",
            translationsInitiated,
            scannedEpisodes,
            episodes.Count,
            skippedNoSubtitles + skippedNoSource + skippedUnknown + skippedUpToDate + skippedTooRecent + skippedMissingPath,
            skippedNoSubtitles, skippedNoSource, skippedUnknown, skippedUpToDate, skippedTooRecent, skippedMissingPath);

        return new AutomationPassStats(
            translationsInitiated,
            scannedEpisodes,
            episodes.Count,
            skippedNoSubtitles,
            skippedNoSource,
            skippedUnknown,
            skippedUpToDate,
            skippedTooRecent,
            excludedCount,
            skippedMissingPath);
    }

    /// <summary>
    /// Self-heals movies whose directory no longer exists: the entry is refreshed
    /// from Radarr (moved/renamed path) or removed when Radarr no longer has it.
    /// Capped per run so a whole missing mount does not stampede the Radarr API.
    /// </summary>
    private async Task ResyncMoviesWithMissingPaths(List<Movie> movies)
    {
        if (movies.Count == 0)
        {
            return;
        }

        var distinct = movies.DistinctBy(movie => movie.Id).ToList();
        _logger.LogWarning(
            "{Count} movies have a missing directory; resyncing up to {Cap} of them against Radarr.",
            distinct.Count, Math.Min(distinct.Count, MaxResyncsPerRun));

        var defaultInclude = await _settingService.GetSetting(SettingKeys.Integration.RadarrDefaultInclude) == "true";
        var resynced = 0;
        foreach (var movie in distinct)
        {
            if (resynced >= MaxResyncsPerRun)
            {
                break;
            }

            try
            {
                resynced++;
                RadarrMovie? radarrMovie;
                try
                {
                    radarrMovie = await _radarrService.GetMovie(movie.RadarrId);
                }
                catch (IntegrationException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    radarrMovie = null;
                }

                if (radarrMovie == null)
                {
                    _logger.LogWarning(
                        "Movie |Green|{Title}|/Green| no longer exists in Radarr — removing the stale entry.",
                        movie.Title);
                    var entity = await _dbContext.Movies
                        .Include(m => m.Images)
                        .FirstOrDefaultAsync(m => m.Id == movie.Id);
                    if (entity != null)
                    {
                        _dbContext.Images.RemoveRange(entity.Images);
                        _dbContext.Movies.Remove(entity);
                        await _dbContext.SaveChangesAsync();
                    }

                    continue;
                }

                await _movieSyncService.SyncMovie(radarrMovie, defaultInclude);
                _logger.LogInformation(
                    "Resynced movie |Green|{Title}|/Green| from Radarr after a missing directory.",
                    movie.Title);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Resync failed for movie |Green|{Title}|/Green| — the stale entry is kept as-is.",
                    movie.Title);
            }
        }
    }

    /// <summary>
    /// Self-heals shows that have episodes with a missing directory: the show is
    /// re-synced from Sonarr (refreshes paths, drops removed episodes) or removed
    /// entirely when Sonarr no longer has it. Capped per run.
    /// </summary>
    private async Task ResyncShowsWithMissingPaths(List<Show> shows)
    {
        if (shows.Count == 0)
        {
            return;
        }

        var distinct = shows.DistinctBy(show => show.Id).ToList();
        _logger.LogWarning(
            "{Count} shows have episodes with a missing directory; resyncing up to {Cap} of them against Sonarr.",
            distinct.Count, Math.Min(distinct.Count, MaxResyncsPerRun));

        List<SonarrShow>? sonarrShows;
        try
        {
            sonarrShows = await _sonarrService.GetShows();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not reach Sonarr to resync shows with missing directories — stale entries are kept as-is.");
            return;
        }

        if (sonarrShows == null)
        {
            _logger.LogWarning(
                "Sonarr is not configured or unreachable — stale show entries are kept as-is. Check the Sonarr integration settings.");
            return;
        }

        var defaultInclude = await _settingService.GetSetting(SettingKeys.Integration.SonarrDefaultInclude) == "true";
        var sonarrShowsById = sonarrShows.ToDictionary(show => show.Id);
        var resynced = 0;
        foreach (var show in distinct)
        {
            if (resynced >= MaxResyncsPerRun)
            {
                break;
            }

            try
            {
                resynced++;
                if (sonarrShowsById.TryGetValue(show.SonarrId, out var sonarrShow))
                {
                    await _showSyncService.SyncShow(sonarrShow, defaultInclude);
                    _logger.LogInformation(
                        "Resynced show |Green|{Title}|/Green| from Sonarr after a missing directory.",
                        show.Title);
                }
                else
                {
                    _logger.LogWarning(
                        "Show |Green|{Title}|/Green| no longer exists in Sonarr — removing the stale entry and its episodes.",
                        show.Title);
                    var entity = await _dbContext.Shows
                        .Include(s => s.Images)
                        .Include(s => s.Seasons)
                            .ThenInclude(season => season.Episodes)
                        .FirstOrDefaultAsync(s => s.Id == show.Id);
                    if (entity != null)
                    {
                        var episodes = entity.Seasons.SelectMany(season => season.Episodes).ToList();
                        _dbContext.Episodes.RemoveRange(episodes);
                        _dbContext.Seasons.RemoveRange(entity.Seasons);
                        _dbContext.Images.RemoveRange(entity.Images);
                        _dbContext.Shows.Remove(entity);
                        await _dbContext.SaveChangesAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Resync failed for show |Green|{Title}|/Green| — the stale entry is kept as-is.",
                    show.Title);
            }
        }
    }

    /// <summary>
    /// Releases translation requests that are stuck in Pending/InProgress beyond the
    /// staleness threshold. Their Hangfire job is gone (crash, manual purge, lost
    /// worker), so they will never complete on their own, yet they block their
    /// target language forever (the processor trusts Pending/InProgress). They are
    /// marked Interrupted and their job deleted so the normal pass re-queues the
    /// target with a fresh request.
    /// </summary>
    /// <returns>The number of requests released.</returns>
    private async Task<int> ReconcileStaleRequests()
    {
        if (_staleRequestHours <= 0)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddHours(-_staleRequestHours);
        var stale = await _dbContext.TranslationRequests
            .Where(translationRequest =>
                (translationRequest.Status == TranslationStatus.Pending
                 || translationRequest.Status == TranslationStatus.InProgress)
                && translationRequest.CreatedAt < cutoff)
            .Select(translationRequest => new
            {
                translationRequest.Id,
                translationRequest.JobId,
                translationRequest.Title,
                translationRequest.TargetLanguage
            })
            .ToListAsync();

        if (stale.Count == 0)
        {
            return 0;
        }

        var freed = 0;
        foreach (var request in stale)
        {
            if (!string.IsNullOrEmpty(request.JobId))
            {
                try
                {
                    // Remove the orphaned Hangfire job so it cannot resurrect
                    // and translate on top of a fresh request.
                    BackgroundJob.Delete(request.JobId);
                }
                catch (Exception)
                {
                    // Job already gone — that is expected after a crash.
                }
            }

            try
            {
                var entity = await _dbContext.TranslationRequests.FindAsync(request.Id);
                if (entity == null || entity.Status != TranslationStatus.Pending &&
                    entity.Status != TranslationStatus.InProgress)
                {
                    continue;
                }

                entity.Status = TranslationStatus.Interrupted;
                entity.CompletedAt = DateTime.UtcNow;
                entity.ErrorMessage =
                    $"Reconciled on {DateTime.UtcNow:yyyy-MM-dd HH:mm}: stuck for over {_staleRequestHours}h, released so the target can be re-queued.";
                await _dbContext.SaveChangesAsync();
                freed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to release stale request {RequestId} ({Title} -> {Language}).",
                    request.Id, request.Title, request.TargetLanguage);
            }
        }

        if (freed > 0)
        {
            _logger.LogWarning(
                "{Count} stuck translation request(s) older than {Hours}h were released (marked interrupted); matching targets will be re-queued this run.",
                freed, _staleRequestHours);
        }

        return freed;
    }

    private int GetProcessingIndex(string key)
    {
        if (!_memoryCache.TryGetValue(key, out int currentIndex))
        {
            currentIndex = 0;
        }
        return currentIndex;
    }
    
    private void SetProcessingIndex(string key, int value)
    {
        var cacheOptions = new MemoryCacheEntryOptions
        {
            Priority = CacheItemPriority.NeverRemove
        };
        
        _memoryCache.Set(key, value, cacheOptions);
    }
}