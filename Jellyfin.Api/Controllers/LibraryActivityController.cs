using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// What the library scan and the metadata refreshes are working on right now (like Plex's activity list),
/// a live log of the latest steps, and the scheduled tasks running.
/// </summary>
[Route("Library/Activity")]
[Authorize(Policy = Policies.RequiresElevation)]
public class LibraryActivityController : BaseJellyfinApiController
{
    private const int MaxCurrent = 16;
    private const int DefaultLog = 40;
    private const int MaxLog = 1000;

    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly IProviderManager _providerManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryActivityController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    public LibraryActivityController(ILibraryManager libraryManager, ITaskManager taskManager, IProviderManager providerManager)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _providerManager = providerManager;
    }

    /// <summary>
    /// Stops the scan of one library. What was already read stays; scanning it again carries on with the rest.
    /// </summary>
    /// <param name="libraryId">The library id.</param>
    /// <response code="200">Scan stopped; returns how many refreshes were stopped.</response>
    /// <returns>How many refreshes were stopped.</returns>
    [HttpPost("{libraryId}/Stop")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<int> StopLibraryScan([FromRoute] Guid libraryId)
    {
        return _providerManager.CancelLibraryRefresh(libraryId);
    }

    /// <summary>
    /// Gets the work being done right now, the latest steps and the running scheduled tasks.
    /// </summary>
    /// <param name="logCount">How many log lines to return (40 by default, at most 1000).</param>
    /// <response code="200">Current activity returned.</response>
    /// <returns>The activity.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LibraryActivityResponse> GetLibraryActivity([FromQuery] int? logCount)
    {
        var logLimit = Math.Clamp(logCount ?? DefaultLog, 0, MaxLog);
        var libraries = new Dictionary<Guid, (Guid Id, string Name)?>();

        var current = LibraryActivity.Snapshot()
            // One row per item, showing its most specific work (trickplay beats analyzing beats checking)
            .GroupBy(e => e.Item?.Id ?? Guid.NewGuid())
            .Select(g => g.OrderByDescending(e => e.Kind).ThenByDescending(e => e.Key).First())
            .OrderByDescending(e => e.Kind)
            .ThenBy(e => e.StartedUtc)
            .Select(e => ToDto(e.Item, e.Title, e.Detail, e.Kind, e.StartedUtc, null, libraries))
            .OfType<LibraryActivityDto>()
            .Take(MaxCurrent)
            .ToList();

        var log = LibraryActivity.RecentLog(logLimit + 100)
            .Select(l => ToDto(l.Item, l.Title, l.Detail, l.Kind, l.TimeUtc, l.LibraryName, libraries))
            .OfType<LibraryActivityDto>()
            .Take(logLimit)
            .ToList();

        var tasks = _taskManager.ScheduledTasks
            .Where(t => t.State == TaskState.Running)
            .Select(t => new RunningTaskDto { Id = t.Id, Key = t.ScheduledTask.Key, Name = t.Name, Progress = t.CurrentProgress })
            .ToList();

        return new LibraryActivityResponse { Current = current, Log = log, Tasks = tasks };
    }

    private LibraryActivityDto? ToDto(BaseItem? item, string? taskTitle, string? detail, LibraryActivityKind kind, DateTime time, string? libraryName, Dictionary<Guid, (Guid Id, string Name)?> libraries)
    {
        if (item is null)
        {
            // Work not tied to one item (post-scan tasks, provider errors)
            return string.IsNullOrEmpty(taskTitle) ? null : new LibraryActivityDto { Title = taskTitle, File = detail, Kind = kind.ToString(), StartedUtc = time };
        }

        if (libraryName is not null)
        {
            // Removed items can no longer be looked up: the library was recorded when it happened
            return new LibraryActivityDto
            {
                ItemId = item.Id,
                Title = taskTitle ?? item.Name,
                File = detail,
                Kind = kind.ToString(),
                LibraryName = libraryName,
                StartedUtc = time
            };
        }

        // The library the item belongs to; folders outside any library (the root, user views) are left out
        var topId = item.GetTopParent()?.Id ?? Guid.Empty;
        if (!libraries.TryGetValue(topId, out var library))
        {
            var folder = _libraryManager.GetCollectionFolders(item).FirstOrDefault();
            library = folder is null ? null : (folder.Id, folder.Name);
            libraries[topId] = library;
        }

        if (library is null)
        {
            return null;
        }

        var title = taskTitle ?? item switch
        {
            Episode e when !string.IsNullOrEmpty(e.SeriesName) => $"{e.SeriesName} · {(e.ParentIndexNumber is int s && e.IndexNumber is int n ? $"T{s}E{n} · " : string.Empty)}{e.Name}",
            Season s when !string.IsNullOrEmpty(s.SeriesName) => $"{s.SeriesName} · {s.Name}",
            _ => item.Name
        };

        return new LibraryActivityDto
        {
            ItemId = item.Id,
            Title = title,
            File = detail ?? (string.IsNullOrEmpty(item.Path) ? null : Path.GetFileName(item.Path.TrimEnd('/', '\\'))),
            Kind = kind.ToString(),
            LibraryId = library.Value.Id,
            LibraryName = library.Value.Name,
            StartedUtc = time
        };
    }

    /// <summary>
    /// The activity: current work, latest steps and running tasks.
    /// </summary>
    public class LibraryActivityResponse
    {
        /// <summary>Gets or sets the work being done right now.</summary>
        public IReadOnlyList<LibraryActivityDto> Current { get; set; } = [];

        /// <summary>Gets or sets the latest steps, newest first.</summary>
        public IReadOnlyList<LibraryActivityDto> Log { get; set; } = [];

        /// <summary>Gets or sets the scheduled tasks running.</summary>
        public IReadOnlyList<RunningTaskDto> Tasks { get; set; } = [];
    }

    /// <summary>
    /// A running scheduled task.
    /// </summary>
    public class RunningTaskDto
    {
        /// <summary>Gets or sets the task id, to stop it.</summary>
        public string? Id { get; set; }

        /// <summary>Gets or sets the task key.</summary>
        public string? Key { get; set; }

        /// <summary>Gets or sets the task name.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets the progress, 0-100.</summary>
        public double? Progress { get; set; }
    }

    /// <summary>
    /// One item (or task) being worked on, or one step of the log.
    /// </summary>
    public class LibraryActivityDto
    {
        /// <summary>Gets or sets the item id, empty for tasks.</summary>
        public Guid? ItemId { get; set; }

        /// <summary>Gets or sets the title.</summary>
        public string? Title { get; set; }

        /// <summary>Gets or sets the file or folder name being handled.</summary>
        public string? File { get; set; }

        /// <summary>Gets or sets the kind of work: Checking, Scanning, Metadata, Images, Analyzing, Trickplay, ChapterImages or PostScan.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>Gets or sets the library id.</summary>
        public Guid? LibraryId { get; set; }

        /// <summary>Gets or sets the library name.</summary>
        public string? LibraryName { get; set; }

        /// <summary>Gets or sets when the work started (or the step happened).</summary>
        public DateTime StartedUtc { get; set; }
    }
}
