using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.MediaSegments;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Detects intros (series) and credits (series and movies) and saves them as media segments,
/// so clients can offer "Skip intro" / "Skip credits".
/// </summary>
public class IntroCreditsDetectionTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly IntroCreditsDetector _detector;
    private readonly IntroCreditsStore _store;
    private readonly ILogger<IntroCreditsDetectionTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroCreditsDetectionTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="localization">The localization manager.</param>
    /// <param name="mediaSegmentManager">The media segment manager.</param>
    /// <param name="detector">The intro / credits detector.</param>
    /// <param name="store">The results store.</param>
    /// <param name="logger">The logger.</param>
    public IntroCreditsDetectionTask(
        ILibraryManager libraryManager,
        ILocalizationManager localization,
        IMediaSegmentManager mediaSegmentManager,
        IntroCreditsDetector detector,
        IntroCreditsStore store,
        ILogger<IntroCreditsDetectionTask> logger)
    {
        _libraryManager = libraryManager;
        _localization = localization;
        _mediaSegmentManager = mediaSegmentManager;
        _detector = detector;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => _localization.GetLocalizedString("TaskDetectIntroCredits");

    /// <inheritdoc />
    public string Description => _localization.GetLocalizedString("TaskDetectIntroCreditsDescription");

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public string Key => "DetectIntroCredits";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        var maxParallel = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

        var episodes = GetItems(BaseItemKind.Episode).OfType<Episode>().ToList();
        var movies = GetItems(BaseItemKind.Movie).ToList();

        var seasonWork = episodes
            .GroupBy(e => e.SeasonId)
            .Select(g => (Episodes: g.ToList(), Targets: g.Where(NeedsAnalysis).Select(e => e.Id).ToHashSet()))
            .Where(s => s.Targets.Count > 0)
            .ToList();
        var movieWork = movies.Where(NeedsAnalysis).ToList();

        var total = seasonWork.Sum(s => s.Targets.Count) + movieWork.Count;
        _logger.LogInformation(
            "Intro/credits detection: {Episodes} episodes in {Seasons} seasons and {Movies} movies to analyze",
            total - movieWork.Count,
            seasonWork.Count,
            movieWork.Count);

        if (total == 0)
        {
            progress.Report(100);
            return;
        }

        var done = 0;
        foreach (var (seasonEpisodes, targets) in seasonWork)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var id in targets)
            {
                ResetIfChanged(id);
            }

            try
            {
                var results = await _detector.AnalyzeSeasonAsync(seasonEpisodes, targets, maxParallel, cancellationToken).ConfigureAwait(false);
                foreach (var (id, record) in results)
                {
                    _store.Set(id, record);
                }

                await _store.SaveAsync(cancellationToken).ConfigureAwait(false);
                foreach (var episode in seasonEpisodes.Where(e => results.ContainsKey(e.Id)))
                {
                    await SaveSegmentsAsync(episode, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Intro/credits detection failed for season {SeasonName} of {SeriesName}", seasonEpisodes[0].SeasonName, seasonEpisodes[0].SeriesName);
            }

            done += targets.Count;
            progress.Report(100.0 * done / total);
        }

        await Parallel.ForEachAsync(
            movieWork,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = cancellationToken },
            async (movie, ct) =>
            {
                ResetIfChanged(movie.Id);
                try
                {
                    var record = await _detector.AnalyzeMovieAsync(movie, ct).ConfigureAwait(false);
                    _store.Set(movie.Id, record);
                    await SaveSegmentsAsync(movie, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Credits detection failed for {Path}", movie.Path);
                }

                var current = Interlocked.Increment(ref done);
                progress.Report(100.0 * current / total);
                if (current % 10 == 0)
                {
                    await _store.SaveAsync(ct).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        await _store.SaveAsync(cancellationToken).ConfigureAwait(false);

        var (analyzed, withIntro, withCredits) = _store.GetCounts();
        _logger.LogInformation(
            "Intro/credits detection finished: {Analyzed} analyzed, {Intro} with intro, {Credits} with credits",
            analyzed,
            withIntro,
            withCredits);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(6).Ticks
        };
    }

    private IEnumerable<BaseItem> GetItems(BaseItemKind kind)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            IsVirtualItem = false,
            Recursive = true,
            SourceTypes = [SourceType.Library],
            DtoOptions = new DtoOptions(false)
        })
        .Where(i => i.IsFileProtocol
            && (i.RunTimeTicks ?? 0) > 0
            && File.Exists(i.Path)
            && IsProviderEnabled(i));
    }

    private bool IsProviderEnabled(BaseItem item)
    {
        var options = _libraryManager.GetLibraryOptions(item);
        return !options.DisabledMediaSegmentProviders.Contains(IntroCreditsSegmentProvider.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    private bool NeedsAnalysis(BaseItem item)
    {
        var record = _store.Get(item.Id);
        return record is null
            || record.Version < IntroCreditsDetector.Version
            || record.RunTimeTicks != (item.RunTimeTicks ?? 0);
    }

    private void ResetIfChanged(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        var record = _store.Get(itemId);
        if (item is not null && record is not null && record.RunTimeTicks != (item.RunTimeTicks ?? 0))
        {
            // The file was replaced: drop the cached fingerprints too.
            _store.Remove(itemId);
        }
    }

    private Task SaveSegmentsAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var options = _libraryManager.GetLibraryOptions(item);
        return _mediaSegmentManager.RunSegmentPluginProviders(item, options, false, cancellationToken);
    }
}
