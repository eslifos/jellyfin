using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;

namespace Emby.Server.Implementations.MediaSegments;

/// <summary>
/// On demand intro / credits detection for a single movie, episode, season or series.
/// </summary>
public class IntroCreditsService : IIntroCreditsService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly IntroCreditsDetector _detector;
    private readonly IntroCreditsStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroCreditsService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSegmentManager">The media segment manager.</param>
    /// <param name="detector">The detector.</param>
    /// <param name="store">The results store.</param>
    public IntroCreditsService(ILibraryManager libraryManager, IMediaSegmentManager mediaSegmentManager, IntroCreditsDetector detector, IntroCreditsStore store)
    {
        _libraryManager = libraryManager;
        _mediaSegmentManager = mediaSegmentManager;
        _detector = detector;
        _store = store;
    }

    /// <inheritdoc />
    public IReadOnlyList<IntroCreditsResultDto> GetResults(BaseItem item)
        => GetPlayableItems(item).Select(ToDto).ToList();

    /// <inheritdoc />
    public async Task<IReadOnlyList<IntroCreditsResultDto>> DetectAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var maxParallel = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        var items = GetPlayableItems(item);

        foreach (var movie in items.OfType<Movie>())
        {
            _store.Remove(movie.Id);
            var record = await _detector.AnalyzeMovieAsync(movie, cancellationToken).ConfigureAwait(false);
            _store.Set(movie.Id, record);
            await SaveSegmentsAsync(movie, cancellationToken).ConfigureAwait(false);
        }

        foreach (var season in items.OfType<Episode>().GroupBy(e => e.SeasonId))
        {
            var targets = season.Select(e => e.Id).ToHashSet();
            foreach (var id in targets)
            {
                _store.Remove(id);
            }

            // Every episode of the season is used as reference, not only the requested ones.
            var allEpisodes = GetEpisodes(season.Key);
            var results = await _detector.AnalyzeSeasonAsync(allEpisodes, targets, maxParallel, cancellationToken).ConfigureAwait(false);
            foreach (var (id, record) in results)
            {
                _store.Set(id, record);
            }

            foreach (var episode in season)
            {
                await SaveSegmentsAsync(episode, cancellationToken).ConfigureAwait(false);
            }
        }

        await _store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return items.Select(ToDto).ToList();
    }

    private List<BaseItem> GetPlayableItems(BaseItem item)
    {
        IEnumerable<BaseItem> items = item switch
        {
            Movie or Episode => [item],
            Season or Series => _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = item.Id,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Episode],
                IsVirtualItem = false,
                DtoOptions = new DtoOptions(false)
            }),
            _ => []
        };

        return items
            .Where(i => i.IsFileProtocol && (i.RunTimeTicks ?? 0) > 0 && File.Exists(i.Path))
            .OrderBy(i => (i as Episode)?.ParentIndexNumber ?? 0)
            .ThenBy(i => i.IndexNumber ?? int.MaxValue)
            .ToList();
    }

    private List<Episode> GetEpisodes(Guid seasonId)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = seasonId,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Episode],
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(false)
        })
        .OfType<Episode>()
        .Where(e => e.IsFileProtocol && File.Exists(e.Path))
        .ToList();
    }

    private Task SaveSegmentsAsync(BaseItem item, CancellationToken cancellationToken)
        => _mediaSegmentManager.RunSegmentPluginProviders(item, _libraryManager.GetLibraryOptions(item), false, cancellationToken);

    private IntroCreditsResultDto ToDto(BaseItem item)
    {
        var record = _store.Get(item.Id);
        var episode = item as Episode;
        return new IntroCreditsResultDto
        {
            ItemId = item.Id,
            Name = item.Name,
            SeriesName = episode?.SeriesName,
            ParentIndexNumber = episode?.ParentIndexNumber,
            IndexNumber = episode?.IndexNumber,
            RunTimeTicks = item.RunTimeTicks ?? 0,
            IsAnalyzed = record is not null,
            AnalyzedUtc = record?.AnalyzedUtc,
            IntroStartTicks = record?.IntroStartTicks,
            IntroEndTicks = record?.IntroEndTicks,
            IntroSource = record?.IntroSource,
            CreditsStartTicks = record?.CreditsStartTicks,
            CreditsEndTicks = record?.CreditsEndTicks,
            CreditsSource = record?.CreditsSource
        };
    }
}
