using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Naming.Common;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Quick scan for new, changed and removed files, in the spirit of Plex's partial scans.
/// It lists every folder of the libraries (only the names inside, no file is opened) and keeps a fingerprint of
/// each one: its date plus the names of what it holds. The folder's date alone is not enough, because on cloud
/// storage (Google Drive through rclone) adding a file to a folder does not change the folder's date. Only the
/// series, seasons or movies whose folders changed are refreshed, on the parallel refresh queue, so new items get
/// their metadata in minutes without a full library scan. Real-time monitoring rarely works on network
/// shares inside Docker, so this is what notices new files there.
/// The first run (or a library seen for the first time) compares every video file on disk with the library and
/// refreshes the folders holding files the library does not have yet, such as episodes that finished uploading
/// after their season was already added.
/// A file replaced in place under the same name is still found by the full library scan.
/// </summary>
public class ScanNewFilesTask : IScheduledTask
{
    private const string SnapshotFileName = "new-files-scan-v2.json";

    /// <summary>The first version kept only folder dates, which miss files added on cloud storage.</summary>
    private const string OldSnapshotFileName = "new-files-scan.json";

    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<ScanNewFilesTask> _logger;
    private readonly HashSet<string> _videoExtensions;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanNewFilesTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ScanNewFilesTask}"/> interface.</param>
    /// <param name="namingOptions">The naming options (which file extensions are videos).</param>
    public ScanNewFilesTask(ILibraryManager libraryManager, IFileSystem fileSystem, IApplicationPaths appPaths, ILogger<ScanNewFilesTask> logger, NamingOptions namingOptions)
    {
        _videoExtensions = new HashSet<string>(namingOptions.VideoFileExtensions, StringComparer.OrdinalIgnoreCase);
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Buscar archivos nuevos";

    /// <inheritdoc />
    public string Description => "Revisa solo las carpetas que cambiaron desde la última vez y agrega o actualiza esos elementos sin escanear toda la biblioteca. Los archivos nuevos aparecen con sus metadatos en pocos minutos.";

    /// <inheritdoc />
    public string Category => "Biblioteca";

    /// <inheritdoc />
    public string Key => "ScanNewFiles";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(15).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // A full scan already looks at everything; the next run picks up whatever it missed
        if (_libraryManager.IsScanRunning)
        {
            _logger.LogInformation("Library scan running, new files check skipped");
            progress.Report(100);
            return;
        }

        var snapshotPath = Path.Combine(_appPaths.DataPath, SnapshotFileName);
        var snapshot = LoadSnapshot(snapshotPath);
        RemoveOldSnapshot();

        var locations = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? [])
            .Where(l => !string.IsNullOrEmpty(l))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var queued = 0;
        for (var i = 0; i < locations.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                queued += await CheckLocationAsync(locations[i], snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking {Location} for new files", locations[i]);
            }

            progress.Report(100.0 * (i + 1) / locations.Count);
        }

        // Forget libraries that were removed
        foreach (var gone in snapshot.Keys.Except(locations, StringComparer.Ordinal).ToList())
        {
            snapshot.Remove(gone);
        }

        SaveSnapshot(snapshotPath, snapshot);

        if (queued > 0)
        {
            _logger.LogInformation("New files check: {Count} items queued for refresh", queued);
        }
    }

    private async Task<int> CheckLocationAsync(string location, Dictionary<string, Dictionary<string, long>> snapshot, CancellationToken cancellationToken)
    {
        // An unreachable share must not look like every folder was removed
        if (!Directory.Exists(location) || !Directory.EnumerateFileSystemEntries(location).Any())
        {
            _logger.LogWarning("{Location} is not reachable or empty, new files check skipped", location);
            return 0;
        }

        var videos = new List<string>();
        var current = ReadFolderFingerprints(location, videos, cancellationToken);

        // A folder added to a library that was never scanned (the scan after adding it was missed): register it
        // and read all of it, otherwise only later changes would ever be added
        var library = _libraryManager.FindByPath(location, true) as Folder;
        if (library is null)
        {
            _logger.LogInformation("New files check: {Location} was never scanned, adding all of it", location);
            await _libraryManager.ValidateTopLibraryFolders(cancellationToken).ConfigureAwait(false);
            library = _libraryManager.FindByPath(location, true) as Folder;
            snapshot[location] = current;
            if (library is null)
            {
                return 0;
            }

            library.ChangedExternally();
            return 1;
        }

        List<string> changed;
        if (snapshot.TryGetValue(location, out var previous))
        {
            changed = current
                .Where(c => !previous.TryGetValue(c.Key, out var before) || before != c.Value)
                .Select(c => c.Key)
                .ToList();
        }
        else
        {
            // First run: a library with nothing added yet is read completely; otherwise every video on disk is
            // compared with the library, so files missed before (a season added while still uploading) come in too
            _logger.LogInformation("New files check: remembered {Count} folders of {Location}", current.Count, location);
            if (!library.Children.Any())
            {
                snapshot[location] = current;
                library.ChangedExternally();
                return 1;
            }

            changed = FoldersWithMissingVideos(location, videos);
        }

        snapshot[location] = current;

        if (changed.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation("New files check: {Count} changed folders in {Location}", changed.Count, location);

        if (_libraryManager.FindByPath(location, true) is not Folder root)
        {
            return 0;
        }

        var directoryService = new DirectoryService(_fileSystem);
        var targets = new Dictionary<Guid, BaseItem>();
        var rootChanged = false;

        foreach (var dir in changed)
        {
            var (target, needsRoot) = ResolveTarget(dir, location, root);

            // A new or removed top level folder, or a new version next to a movie: the library folder
            // itself has to be re-read
            rootChanged |= needsRoot;
            if (target is not null)
            {
                targets.TryAdd(target.Id, target);
            }
        }

        if (rootChanged)
        {
            var before = root.Children.Select(c => c.Id).ToHashSet();
            await root.ValidateDirectChildren(directoryService, cancellationToken).ConfigureAwait(false);

            // ValidateDirectChildren cleared the cached list, so this reads the children again
            foreach (var child in root.Children ?? [])
            {
                if (!before.Contains(child.Id))
                {
                    targets.TryAdd(child.Id, child);
                }
            }
        }

        // A refresh of a folder also covers everything inside it
        var folderIds = targets.Values.OfType<Folder>().Select(f => f.Id).ToHashSet();
        var queued = 0;
        foreach (var item in targets.Values)
        {
            if (item.GetParents().Any(p => folderIds.Contains(p.Id)))
            {
                continue;
            }

            _logger.LogInformation("New files check: {Name} ({Path}) will be refreshed", item.Name, item.Path);
            item.ChangedExternally();
            queued++;
        }

        return queued;
    }

    /// <summary>
    /// Finds the library item to refresh for a changed folder: the folder's own item (a series or season),
    /// or the item stored directly in it (a movie in its own folder). NeedsRoot is true when the library folder
    /// has to be re-read (a top level change, or files next to a movie, which the library folder resolves).
    /// </summary>
    private (BaseItem? Target, bool NeedsRoot) ResolveTarget(string dir, string location, Folder root)
    {
        var top = location.TrimEnd('/', '\\');
        var path = dir;
        while (!string.IsNullOrEmpty(path) && path.Length >= top.Length)
        {
            if (string.Equals(path.TrimEnd('/', '\\'), top, StringComparison.Ordinal))
            {
                return (null, true);
            }

            if (_libraryManager.FindByPath(path, true) is { } folderItem && !folderItem.Id.Equals(root.Id))
            {
                return (folderItem, false);
            }

            // A movie (or video) stored in its own folder: its item points to the file inside
            var prefix = path.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            var stored = root.Children.FirstOrDefault(c => c.Path is not null && c.Path.StartsWith(prefix, StringComparison.Ordinal));
            if (stored is not null)
            {
                return (stored, stored is not Folder);
            }

            path = Path.GetDirectoryName(path) ?? string.Empty;
        }

        return (null, true);
    }

    /// <summary>NAS and system folders (Synology @eaDir, #recycle, hidden folders) that change on their own.</summary>
    private static bool IsIgnored(string fullPath, int locationLength)
    {
        var relative = fullPath.AsSpan(Math.Min(locationLength, fullPath.Length));
        foreach (var part in relative.ToString().Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part[0] is '@' or '.' or '#' || string.Equals(part, "lost+found", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The folders holding video files that are not in the library yet.
    /// </summary>
    private List<string> FoldersWithMissingVideos(string location, List<string> videos)
    {
        // Every video file the library knows, including the extra parts and versions of a movie
        var known = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IsFolder = false,
                IncludeOwnedItems = true,
                DtoOptions = new DtoOptions(false) { EnableImages = false }
            })
            .Select(i => i.Path)
            .Where(p => !string.IsNullOrEmpty(p) && p.StartsWith(location, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var missing = videos
            .Where(v => !known.Contains(v))
            .Select(v => Path.GetDirectoryName(v) ?? location)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (missing.Count > 0)
        {
            _logger.LogInformation("New files check: {Count} folders of {Location} hold videos missing from the library", missing.Count, location);
        }

        return missing;
    }

    /// <summary>
    /// Every folder of a library with its fingerprint: its date mixed with the names of the files and folders in it.
    /// Reading the names costs the same directory listings the walk already needs. The video files found are added
    /// to <paramref name="videos"/>.
    /// </summary>
    private Dictionary<string, long> ReadFolderFingerprints(string location, List<string> videos, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(location));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            var names = new List<string>();
            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
                {
                    if (entry is DirectoryInfo sub)
                    {
                        if (IsIgnored(sub.FullName, location.Length))
                        {
                            continue;
                        }

                        pending.Push(sub);
                    }
                    else if (_videoExtensions.Contains(entry.Extension) && !IsIgnored(entry.FullName, location.Length))
                    {
                        videos.Add(entry.FullName);
                    }

                    names.Add(entry.Name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be read now keeps no fingerprint, so it is not taken as changed or removed
                continue;
            }

            names.Sort(StringComparer.Ordinal);
            result[dir.FullName] = Fingerprint(dir.LastWriteTimeUtc.Ticks, names);
        }

        return result;
    }

    /// <summary>A stable 64-bit FNV-1a hash (the same every run, unlike string.GetHashCode).</summary>
    private static long Fingerprint(long ticks, List<string> names)
    {
        const ulong Prime = 1099511628211;
        var hash = 14695981039346656037 ^ (ulong)ticks;
        hash *= Prime;
        foreach (var name in names)
        {
            foreach (var ch in name)
            {
                hash = (hash ^ ch) * Prime;
            }

            hash = (hash ^ '/') * Prime;
        }

        return unchecked((long)hash);
    }

    private Dictionary<string, Dictionary<string, long>> LoadSnapshot(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(File.ReadAllText(path), JsonDefaults.Options)
                    ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read {Path}, starting over", path);
        }

        return [];
    }

    private void RemoveOldSnapshot()
    {
        var oldPath = Path.Combine(_appPaths.DataPath, OldSnapshotFileName);
        try
        {
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", oldPath);
        }
    }

    private void SaveSnapshot(string path, Dictionary<string, Dictionary<string, long>> snapshot)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonDefaults.Options));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save {Path}", path);
        }
    }
}
