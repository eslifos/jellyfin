using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// What a library item is going through right now.
/// </summary>
public enum LibraryActivityKind
{
    /// <summary>Checking whether an item changed (most are skipped quickly).</summary>
    Checking = 0,

    /// <summary>Reading a folder from the disk.</summary>
    Scanning = 1,

    /// <summary>Downloading metadata from the providers.</summary>
    Metadata = 2,

    /// <summary>Downloading images.</summary>
    Images = 3,

    /// <summary>Reading the file with ffprobe (streams, resolution, length).</summary>
    Analyzing = 4,

    /// <summary>Creating the seek preview thumbnails (trickplay).</summary>
    Trickplay = 5,

    /// <summary>Extracting chapter images.</summary>
    ChapterImages = 6,

    /// <summary>A task that runs after the scan (people, genres, collections, database...).</summary>
    PostScan = 7,

    /// <summary>A new item was added to the library.</summary>
    Added = 8,

    /// <summary>An item was removed from the library (its file is gone or was renamed).</summary>
    Removed = 9,

    /// <summary>Something failed (a provider, a refresh...).</summary>
    Error = 10
}

/// <summary>
/// Live view of the library work, for the dashboard (like Plex's activity list): the items being worked on
/// right now, and a short log of the latest steps so even items handled in milliseconds can be followed.
/// Cheap to update: a dictionary insert / removal and a bounded queue append per step.
/// </summary>
public static class LibraryActivity
{
    private const int LogSize = 1000;

    private static readonly ConcurrentDictionary<long, Entry> _active = new();
    private static readonly ConcurrentQueue<LogLine> _log = new();
    private static long _nextKey;

    /// <summary>
    /// Records that work on an item started; dispose the result when it ends.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="kind">The kind of work.</param>
    /// <returns>The entry, to update or dispose.</returns>
    public static Entry Begin(BaseItem item, LibraryActivityKind kind)
        => Add(new Entry(Interlocked.Increment(ref _nextKey), item, null, kind));

    /// <summary>
    /// Records that work not tied to one item started (a post-scan task); dispose the result when it ends.
    /// </summary>
    /// <param name="title">What is being done.</param>
    /// <param name="kind">The kind of work.</param>
    /// <returns>The entry, to update or dispose.</returns>
    public static Entry Begin(string title, LibraryActivityKind kind)
        => Add(new Entry(Interlocked.Increment(ref _nextKey), null, title, kind));

    /// <summary>
    /// Adds a one-off event to the log (an item added or removed, an error).
    /// </summary>
    /// <param name="item">The item, if any.</param>
    /// <param name="kind">The kind of event.</param>
    /// <param name="title">The title; the item name when empty.</param>
    /// <param name="detail">The file, path or error message.</param>
    /// <param name="libraryName">The library, when the item can no longer be looked up (removed items).</param>
    public static void Note(BaseItem? item, LibraryActivityKind kind, string? title = null, string? detail = null, string? libraryName = null)
        => Append(new LogLine(DateTime.UtcNow, kind, item, title ?? item?.Name, detail, libraryName));

    /// <summary>
    /// Gets the current entries, newest first. Entries older than a day are left out, in case one was not disposed.
    /// </summary>
    /// <returns>The entries.</returns>
    public static IReadOnlyList<Entry> Snapshot()
    {
        var limit = DateTime.UtcNow.AddDays(-1);
        return _active.Values.Where(e => e.StartedUtc > limit).OrderByDescending(e => e.Key).ToList();
    }

    /// <summary>
    /// Gets the latest steps, newest first.
    /// </summary>
    /// <param name="count">How many.</param>
    /// <returns>The log lines.</returns>
    public static IReadOnlyList<LogLine> RecentLog(int count)
        => _log.Reverse().Take(count).ToList();

    private static Entry Add(Entry entry)
    {
        _active[entry.Key] = entry;
        AppendLog(entry);
        return entry;
    }

    private static void AppendLog(Entry entry)
        => Append(new LogLine(DateTime.UtcNow, entry.Kind, entry.Item, entry.Title, entry.Detail, null));

    private static void Append(LogLine line)
    {
        _log.Enqueue(line);
        while (_log.Count > LogSize && _log.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// One step in the log.
    /// </summary>
    /// <param name="TimeUtc">When it happened.</param>
    /// <param name="Kind">The kind of work.</param>
    /// <param name="Item">The item, if any.</param>
    /// <param name="Title">The title of work not tied to an item.</param>
    /// <param name="Detail">The file or folder being handled.</param>
    /// <param name="LibraryName">The library, for items that can no longer be looked up.</param>
    public sealed record LogLine(DateTime TimeUtc, LibraryActivityKind Kind, BaseItem? Item, string? Title, string? Detail, string? LibraryName);

    /// <summary>
    /// One item (or task) being worked on.
    /// </summary>
    public sealed class Entry : IDisposable
    {
        private LibraryActivityKind _kind;

        internal Entry(long key, BaseItem? item, string? title, LibraryActivityKind kind)
        {
            Key = key;
            Item = item;
            Title = title;
            _kind = kind;
            StartedUtc = DateTime.UtcNow;
        }

        /// <summary>Gets the order in which entries started.</summary>
        public long Key { get; }

        /// <summary>Gets the item, or null for work not tied to one item.</summary>
        public BaseItem? Item { get; }

        /// <summary>Gets the title of work not tied to an item.</summary>
        public string? Title { get; }

        /// <summary>Gets or sets the current kind of work; a change is added to the log.</summary>
        public LibraryActivityKind Kind
        {
            get => _kind;
            set
            {
                if (_kind != value)
                {
                    _kind = value;
                    AppendLog(this);
                }
            }
        }

        /// <summary>Gets or sets the file or folder inside the item being handled (while scanning a folder).</summary>
        public string? Detail { get; set; }

        /// <summary>Gets when the work started.</summary>
        public DateTime StartedUtc { get; }

        /// <inheritdoc />
        public void Dispose() => _active.TryRemove(Key, out _);
    }
}
