using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;

namespace MediaBrowser.Providers.Plugins.Tmdb.TV
{
    /// <summary>
    /// Remembers when each series was last checked against TMDb for missing / upcoming episodes, and how many
    /// episode files it had then, so library scans only check series that changed or are due.
    /// Stored in the TMDb plugin data folder; losing it only means every series is checked once more.
    /// </summary>
    internal static class TmdbMissingEpisodeCheckStore
    {
        private static readonly ConcurrentDictionary<Guid, Entry> _entries = new();
        private static readonly object _loadLock = new();
        private static int _saveScheduled;
        private static bool _loaded;

        private static string? FilePath
        {
            get
            {
                var folder = Plugin.Instance?.DataFolderPath;
                return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "missing-episode-checks.json");
            }
        }

        public static bool TryGet(Guid seriesId, out Entry entry)
        {
            EnsureLoaded();
            return _entries.TryGetValue(seriesId, out entry!);
        }

        public static void Set(Guid seriesId, int episodeFiles)
        {
            EnsureLoaded();
            _entries[seriesId] = new Entry { CheckedUtc = DateTime.UtcNow, EpisodeFiles = episodeFiles };
            ScheduleSave();
        }

        /// <summary>Makes the next refresh of the series check it against TMDb.</summary>
        public static void Forget(Guid seriesId)
        {
            EnsureLoaded();
            _entries.TryRemove(seriesId, out _);
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            lock (_loadLock)
            {
                if (_loaded)
                {
                    return;
                }

                try
                {
                    var path = FilePath;
                    if (path is not null && File.Exists(path))
                    {
                        var stored = JsonSerializer.Deserialize<Dictionary<Guid, Entry>>(File.ReadAllText(path), JsonDefaults.Options);
                        foreach (var (id, entry) in stored ?? new Dictionary<Guid, Entry>())
                        {
                            _entries.TryAdd(id, entry);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // start over: every series is checked once more
                }

                _loaded = true;
            }
        }

        /// <summary>Writes at most every 30 seconds, so a scan of thousands of series does not rewrite the file each time.</summary>
        private static void ScheduleSave()
        {
            if (Interlocked.Exchange(ref _saveScheduled, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                Interlocked.Exchange(ref _saveScheduled, 0);
                try
                {
                    var path = FilePath;
                    if (path is null)
                    {
                        return;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temp = path + ".tmp";
                    await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new Dictionary<Guid, Entry>(_entries), JsonDefaults.Options)).ConfigureAwait(false);
                    File.Move(temp, path, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // try again on the next change
                }
            });
        }

        /// <summary>State of one series at its last check.</summary>
        internal sealed class Entry
        {
            public DateTime CheckedUtc { get; set; }

            public int EpisodeFiles { get; set; }
        }
    }
}
