using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Api.Models.LibraryDtos;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Per-library statistics (counts, size, runtime, last scan and last added item) for the dashboard.
/// </summary>
[Route("Library/Stats")]
[Authorize(Policy = Policies.RequiresElevation)]
public class LibraryStatsController : BaseJellyfinApiController
{
    private static readonly TimeSpan _cacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<Guid, LibraryStatsDto> _cache = new();

    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryStatsController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    public LibraryStatsController(ILibraryManager libraryManager, ITaskManager taskManager)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
    }

    /// <summary>
    /// Gets statistics for every library.
    /// </summary>
    /// <param name="refresh">Ignore the cached values and compute them again.</param>
    /// <response code="200">Statistics returned.</response>
    /// <returns>The statistics of each library.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<LibraryStatsDto>> GetLibraryStats([FromQuery] bool refresh = false)
    {
        var lastLibraryScan = _taskManager.ScheduledTasks
            .FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, "RefreshLibrary", StringComparison.Ordinal))?
            .LastExecutionResult?.EndTimeUtc;

        var result = new List<LibraryStatsDto>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out var id))
            {
                continue;
            }

            if (!refresh
                && _cache.TryGetValue(id, out var cached)
                && DateTime.UtcNow - cached.ComputedAt < _cacheLifetime)
            {
                result.Add(cached);
                continue;
            }

            var stats = Compute(id, lastLibraryScan);
            if (stats is not null)
            {
                _cache[id] = stats;
                result.Add(stats);
            }
        }

        return result;
    }

    private LibraryStatsDto? Compute(Guid libraryId, DateTime? lastLibraryScan)
    {
        var library = _libraryManager.GetItemById(libraryId);
        if (library is null)
        {
            return null;
        }

        var counts = _libraryManager.GetItemCounts(new InternalItemsQuery
        {
            ParentId = libraryId,
            Recursive = true,
            IsVirtualItem = false
        });

        var seasonCount = _libraryManager.GetCount(new InternalItemsQuery
        {
            ParentId = libraryId,
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = [BaseItemKind.Season]
        });

        var files = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = libraryId,
            Recursive = true,
            IsFolder = false,
            IsVirtualItem = false
        });

        var latest = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = libraryId,
            Recursive = true,
            IsFolder = false,
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
            Limit = 1
        }).FirstOrDefault();

        var scanDate = library.DateLastRefreshed > DateTime.MinValue.AddYears(1)
            ? library.DateLastRefreshed
            : lastLibraryScan;
        if (lastLibraryScan.HasValue && (!scanDate.HasValue || lastLibraryScan.Value > scanDate.Value))
        {
            scanDate = lastLibraryScan;
        }

        return new LibraryStatsDto
        {
            ItemId = libraryId,
            FileCount = files.Count,
            ItemCount = counts.ItemCount,
            MovieCount = counts.MovieCount,
            SeriesCount = counts.SeriesCount,
            SeasonCount = seasonCount,
            EpisodeCount = counts.EpisodeCount,
            AlbumCount = counts.AlbumCount,
            ArtistCount = counts.ArtistCount,
            SongCount = counts.SongCount,
            MusicVideoCount = counts.MusicVideoCount,
            BookCount = counts.BookCount,
            BoxSetCount = counts.BoxSetCount,
            PhotoCount = files.Count(i => i.GetBaseItemKind() == BaseItemKind.Photo),
            VideoCount = files.Count(i => i.GetBaseItemKind() == BaseItemKind.Video),
            TotalSize = files.Sum(i => i.Size ?? 0),
            TotalRunTimeTicks = files.Sum(i => i.RunTimeTicks ?? 0),
            LastScanDate = scanDate,
            LastAddedDate = latest?.DateCreated,
            LastAddedName = latest switch
            {
                Episode e when !string.IsNullOrEmpty(e.SeriesName) => $"{e.SeriesName} · {e.Name}",
                null => null,
                _ => latest.Name
            },
            LastAddedId = latest?.Id,
            ComputedAt = DateTime.UtcNow
        };
    }
}
