using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.PlaybackStats;
using MediaBrowser.Model.PlaybackStats;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PlaybackStats;

/// <summary>
/// Stores the playback history in its own SQLite file (playback-stats.db in the data folder),
/// separated from jellyfin.db so recording never competes with library writes.
/// </summary>
public sealed class PlaybackStatsStore : IPlaybackStatsService
{
    private const string Columns = "Id, UserId, UserName, ItemId, ItemName, ItemType, SeriesId, SeriesName, ParentIndexNumber, IndexNumber, ProductionYear, RunTimeTicks, StartedUtc, StoppedUtc, WatchedSeconds, PositionTicks, Completed, Client, DeviceName, RemoteAddress, PlayMethod, TranscodeReasons, VideoCodec, AudioCodec, Height";

    private readonly ILogger<PlaybackStatsStore> _logger;
    private readonly string _connectionString;
    private readonly Lock _writeLock = new();
    private readonly Lock _initLock = new();
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStatsStore"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackStatsStore(IApplicationPaths appPaths, ILogger<PlaybackStatsStore> logger)
    {
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(appPaths.DataPath, "playback-stats.db"),
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
    }

    /// <summary>
    /// Inserts a new playback and returns its id.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The row id.</returns>
    public long Insert(PlaybackHistoryEntry entry)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO Plays (" + Columns[4..] + ") VALUES (@UserId, @UserName, @ItemId, @ItemName, @ItemType, @SeriesId, @SeriesName, @ParentIndexNumber, @IndexNumber, @ProductionYear, @RunTimeTicks, @StartedUtc, @StoppedUtc, @WatchedSeconds, @PositionTicks, @Completed, @Client, @DeviceName, @RemoteAddress, @PlayMethod, @TranscodeReasons, @VideoCodec, @AudioCodec, @Height); SELECT last_insert_rowid();";
            Bind(cmd, entry);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// Updates the changing fields of a playback.
    /// </summary>
    /// <param name="entry">The entry.</param>
    public void Update(PlaybackHistoryEntry entry)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Plays SET StoppedUtc = @StoppedUtc, WatchedSeconds = @WatchedSeconds, PositionTicks = @PositionTicks, Completed = @Completed, PlayMethod = @PlayMethod, TranscodeReasons = @TranscodeReasons, VideoCodec = @VideoCodec, AudioCodec = @AudioCodec, Height = @Height, RemoteAddress = @RemoteAddress WHERE Id = @Id";
            Bind(cmd, entry);
            cmd.Parameters.AddWithValue("@Id", entry.Id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Deletes a playback (used for very short plays).
    /// </summary>
    /// <param name="id">The row id.</param>
    public void Delete(long id)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Plays WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Closes playbacks left open by a server stop or crash.
    /// </summary>
    public void CloseOrphans()
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Plays SET StoppedUtc = StartedUtc WHERE StoppedUtc IS NULL";
            var n = cmd.ExecuteNonQuery();
            if (n > 0)
            {
                _logger.LogInformation("Closed {Count} playbacks left open by the last shutdown", n);
            }
        }
    }

    /// <inheritdoc />
    public PlaybackStatsOverview GetOverview(int days, string? timeZone)
    {
        days = Math.Clamp(days, 1, 3650);
        var tz = ResolveZone(timeZone);
        var nowUtc = DateTime.UtcNow;
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz).Date;
        var startLocal = todayLocal.AddDays(-(days - 1));
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, tz);
        var prevStartUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal.AddDays(-days), tz);

        var rows = Query("WHERE StartedUtc >= @From ORDER BY StartedUtc", cmd => cmd.Parameters.AddWithValue("@From", ToDb(prevStartUtc)));
        var current = rows.Where(r => r.StartedUtc >= startUtc).ToList();
        var previous = rows.Where(r => r.StartedUtc < startUtc).ToList();

        var daily = new Dictionary<DateTime, PlaybackStatsDay>();
        for (var d = startLocal; d <= todayLocal; d = d.AddDays(1))
        {
            daily[d] = new PlaybackStatsDay { Date = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        }

        var byHour = new int[24];
        var byWeekday = new long[7];
        foreach (var r in current)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(r.StartedUtc, tz);
            byHour[local.Hour]++;
            byWeekday[((int)local.DayOfWeek + 6) % 7] += r.WatchedSeconds;
            if (daily.TryGetValue(local.Date, out var day))
            {
                day.Plays++;
                switch (Kind(r.ItemType))
                {
                    case "Episode": day.EpisodeSeconds += r.WatchedSeconds; break;
                    case "Movie": day.MovieSeconds += r.WatchedSeconds; break;
                    default: day.OtherSeconds += r.WatchedSeconds; break;
                }
            }
        }

        return new PlaybackStatsOverview
        {
            Days = days,
            Current = Totals(current),
            Previous = Totals(previous),
            Daily = daily.Values.ToList(),
            PlaysByHour = byHour,
            SecondsByWeekday = byWeekday,
            TopUsers = Rank(current, r => r.UserId.ToString(), r => r.UserId, r => r.UserName, 10),
            TopSeries = Rank(current.Where(r => Kind(r.ItemType) == "Episode" && r.SeriesId.HasValue), r => r.SeriesId!.Value.ToString(), r => r.SeriesId, r => r.SeriesName, 10),
            TopMovies = Rank(current.Where(r => Kind(r.ItemType) == "Movie"), r => r.ItemId.ToString(), r => r.ItemId, r => r.ProductionYear is int y ? $"{r.ItemName} ({y})" : r.ItemName, 10),
            TopClients = Rank(current, r => r.Client ?? "?", _ => null, r => r.Client, 8),
            PlayMethods = Rank(current, r => r.PlayMethod ?? "Unknown", _ => null, r => r.PlayMethod ?? "Unknown", 5)
        };
    }

    /// <inheritdoc />
    public PlaybackHistoryResult GetHistory(int startIndex, int limit, Guid? userId, string? itemType, string? searchTerm)
    {
        var where = new List<string>();
        void AddFilters(SqliteCommand cmd)
        {
            if (userId.HasValue)
            {
                cmd.Parameters.AddWithValue("@UserId", userId.Value.ToString());
            }

            if (!string.IsNullOrEmpty(searchTerm))
            {
                cmd.Parameters.AddWithValue("@Search", "%" + searchTerm + "%");
            }
        }

        if (userId.HasValue)
        {
            where.Add("UserId = @UserId");
        }

        switch (itemType)
        {
            case "Episode": where.Add("ItemType = 'Episode'"); break;
            case "Movie": where.Add("ItemType = 'Movie'"); break;
            case "Other": where.Add("ItemType NOT IN ('Episode', 'Movie')"); break;
        }

        if (!string.IsNullOrEmpty(searchTerm))
        {
            where.Add("(ItemName LIKE @Search OR SeriesName LIKE @Search OR UserName LIKE @Search OR Client LIKE @Search OR DeviceName LIKE @Search)");
        }

        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;

        int total;
        using (var connection = Open())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Plays " + whereSql;
            AddFilters(cmd);
            total = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var items = Query(
            whereSql + " ORDER BY StartedUtc DESC LIMIT @Limit OFFSET @Offset",
            cmd =>
            {
                AddFilters(cmd);
                cmd.Parameters.AddWithValue("@Limit", Math.Clamp(limit, 1, 2000));
                cmd.Parameters.AddWithValue("@Offset", Math.Max(0, startIndex));
            });

        return new PlaybackHistoryResult { Items = items, TotalRecordCount = total };
    }

    /// <inheritdoc />
    public IReadOnlyList<PlaybackUserStats> GetUserStats(string? timeZone)
    {
        var tz = ResolveZone(timeZone);
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
        var startLocal = todayLocal.AddDays(-29);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, tz);

        var result = new List<PlaybackUserStats>();
        using var connection = Open();

        // All time totals per user (cheap, done in SQL).
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT UserId, COUNT(*), SUM(WatchedSeconds), MIN(StartedUtc), MAX(StartedUtc) FROM Plays GROUP BY UserId";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new PlaybackUserStats
                {
                    UserId = Guid.Parse(r.GetString(0)),
                    Plays = r.GetInt32(1),
                    WatchedSeconds = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                    FirstPlayUtc = r.IsDBNull(3) ? null : FromDb(r.GetString(3)),
                    LastPlayUtc = r.IsDBNull(4) ? null : FromDb(r.GetString(4))
                });
            }
        }

        // Last 30 days, split per day, and the favourite client.
        var recent = Query("WHERE StartedUtc >= @From", cmd => cmd.Parameters.AddWithValue("@From", ToDb(startUtc)))
            .GroupBy(e => e.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var user in result)
        {
            var daily = new long[30];
            if (recent.TryGetValue(user.UserId, out var plays))
            {
                foreach (var p in plays)
                {
                    var day = (TimeZoneInfo.ConvertTimeFromUtc(p.StartedUtc, tz).Date - startLocal).Days;
                    if (day is >= 0 and < 30)
                    {
                        daily[day] += p.WatchedSeconds;
                    }
                }

                user.Plays30 = plays.Count;
                user.WatchedSeconds30 = plays.Sum(p => p.WatchedSeconds);
            }

            user.Daily30 = daily;

            var last = Query("WHERE UserId = @UserId ORDER BY StartedUtc DESC LIMIT 1", cmd => cmd.Parameters.AddWithValue("@UserId", user.UserId.ToString()));
            user.LastPlay = last.FirstOrDefault();
            user.UserName = user.LastPlay?.UserName;

            using var top = connection.CreateCommand();
            top.CommandText = "SELECT Client FROM Plays WHERE UserId = @UserId AND Client IS NOT NULL GROUP BY Client ORDER BY COUNT(*) DESC LIMIT 1";
            top.Parameters.AddWithValue("@UserId", user.UserId.ToString());
            user.TopClient = top.ExecuteScalar() as string;
        }

        return result.OrderByDescending(u => u.LastPlayUtc).ToList();
    }

    /// <inheritdoc />
    public PlaybackPersonalStats GetPersonalStats(Guid userId, string? timeZone)
    {
        var tz = ResolveZone(timeZone);
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
        var since30 = todayLocal.AddDays(-29);

        var plays = Query("WHERE UserId = @UserId ORDER BY StartedUtc", cmd => cmd.Parameters.AddWithValue("@UserId", userId.ToString()));
        var stats = new PlaybackPersonalStats { LastPlay = plays.Count > 0 ? plays[^1] : null };
        if (plays.Count == 0)
        {
            return stats;
        }

        var localDays = plays.Select(p => TimeZoneInfo.ConvertTimeFromUtc(p.StartedUtc, tz).Date).ToList();

        // Last 30 days.
        for (var i = 0; i < plays.Count; i++)
        {
            if (localDays[i] >= since30)
            {
                stats.Plays30++;
                stats.WatchedSeconds30 += plays[i].WatchedSeconds;
                if (plays[i].Completed)
                {
                    stats.Finished30++;
                }
            }
        }

        // Streaks over the distinct days with plays.
        var days = localDays.Distinct().OrderBy(d => d).ToList();
        int best = 1, run = 1;
        for (var i = 1; i < days.Count; i++)
        {
            run = (days[i] - days[i - 1]).Days == 1 ? run + 1 : 1;
            best = Math.Max(best, run);
        }

        stats.BestStreakDays = best;
        var lastDay = days[^1];
        stats.CurrentStreakDays = (todayLocal - lastDay).Days <= 1 ? run : 0;

        // Biggest binge: most distinct episodes of one series watched in one local day.
        var binge = plays
            .Select((p, i) => (Play: p, Day: localDays[i]))
            .Where(x => x.Play.ItemType == "Episode" && x.Play.SeriesId.HasValue)
            .GroupBy(x => (x.Play.SeriesId!.Value, x.Day))
            .Select(g => (Group: g, Episodes: g.Select(x => x.Play.ItemId).Distinct().Count()))
            .OrderByDescending(x => x.Episodes)
            .ThenByDescending(x => x.Group.Key.Day)
            .FirstOrDefault();
        if (binge.Group is not null && binge.Episodes > 1)
        {
            var first = binge.Group.First().Play;
            stats.BingeSeriesId = first.SeriesId;
            stats.BingeSeriesName = first.SeriesName;
            stats.BingeEpisodes = binge.Episodes;
            stats.BingeDate = binge.Group.Key.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // All time totals.
        stats.TotalPlays = plays.Count;
        stats.TotalWatchedSeconds = plays.Sum(p => p.WatchedSeconds);
        stats.TotalEpisodeSeconds = plays.Where(p => Kind(p.ItemType) == "Episode").Sum(p => p.WatchedSeconds);
        stats.TotalMovieSeconds = plays.Where(p => Kind(p.ItemType) == "Movie").Sum(p => p.WatchedSeconds);
        stats.EpisodesFinished = plays.Where(p => p.Completed && Kind(p.ItemType) == "Episode").Select(p => p.ItemId).Distinct().Count();
        stats.MoviesFinished = plays.Where(p => p.Completed && Kind(p.ItemType) == "Movie").Select(p => p.ItemId).Distinct().Count();
        stats.ActiveDays = days.Count;
        stats.FirstPlayUtc = plays[0].StartedUtc;

        // Last 30 days, per day and kind.
        var daily = new Dictionary<DateTime, PlaybackStatsDay>();
        for (var d = since30; d <= todayLocal; d = d.AddDays(1))
        {
            daily[d] = new PlaybackStatsDay { Date = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        }

        var byHour = new int[24];
        var byWeekday = new long[7];
        for (var i = 0; i < plays.Count; i++)
        {
            var p = plays[i];
            var local = TimeZoneInfo.ConvertTimeFromUtc(p.StartedUtc, tz);
            byHour[local.Hour]++;
            byWeekday[((int)local.DayOfWeek + 6) % 7] += p.WatchedSeconds;
            if (daily.TryGetValue(localDays[i], out var day))
            {
                day.Plays++;
                switch (Kind(p.ItemType))
                {
                    case "Episode": day.EpisodeSeconds += p.WatchedSeconds; break;
                    case "Movie": day.MovieSeconds += p.WatchedSeconds; break;
                    default: day.OtherSeconds += p.WatchedSeconds; break;
                }
            }
        }

        stats.Daily = daily.Values.ToList();
        stats.PlaysByHour = byHour;
        stats.SecondsByWeekday = byWeekday;
        stats.TopSeries = Rank(plays.Where(r => Kind(r.ItemType) == "Episode" && r.SeriesId.HasValue), r => r.SeriesId!.Value.ToString(), r => r.SeriesId, r => r.SeriesName, 8);
        stats.TopMovies = Rank(plays.Where(r => Kind(r.ItemType) == "Movie"), r => r.ItemId.ToString(), r => r.ItemId, r => r.ProductionYear is int y ? $"{r.ItemName} ({y})" : r.ItemName, 8);
        stats.TopClients = Rank(plays, r => r.Client ?? "?", _ => null, r => r.Client, 5);
        stats.Recent = plays.AsEnumerable().Reverse().Take(12).ToList();

        return stats;
    }

    private static string Kind(string? itemType) => itemType is "Episode" or "Movie" ? itemType : "Other";

    private static TimeZoneInfo ResolveZone(string? timeZone)
    {
        if (!string.IsNullOrWhiteSpace(timeZone))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // fall back to the server zone
            }
        }

        return TimeZoneInfo.Local;
    }

    private static PlaybackStatsTotals Totals(List<PlaybackHistoryEntry> rows)
    {
        if (rows.Count == 0)
        {
            return new PlaybackStatsTotals();
        }

        var watched = rows.Sum(r => r.WatchedSeconds);
        return new PlaybackStatsTotals
        {
            Plays = rows.Count,
            WatchedSeconds = watched,
            ActiveUsers = rows.Select(r => r.UserId).Distinct().Count(),
            AverageSessionSeconds = watched / rows.Count,
            CompletedPercent = Math.Round(100.0 * rows.Count(r => r.Completed) / rows.Count, 1),
            TranscodePercent = Math.Round(100.0 * rows.Count(r => r.PlayMethod == "Transcode") / rows.Count, 1)
        };
    }

    private static List<PlaybackStatsRank> Rank(
        IEnumerable<PlaybackHistoryEntry> rows,
        Func<PlaybackHistoryEntry, string> key,
        Func<PlaybackHistoryEntry, Guid?> id,
        Func<PlaybackHistoryEntry, string?> name,
        int take)
    {
        return rows
            .GroupBy(key)
            .Select(g =>
            {
                var last = g.MaxBy(r => r.StartedUtc)!;
                return new PlaybackStatsRank
                {
                    Id = id(last),
                    Name = name(last),
                    Plays = g.Count(),
                    WatchedSeconds = g.Sum(r => r.WatchedSeconds),
                    Users = g.Select(r => r.UserId).Distinct().Count(),
                    LastPlayedUtc = last.StoppedUtc ?? last.StartedUtc
                };
            })
            .OrderByDescending(r => r.WatchedSeconds)
            .ThenByDescending(r => r.Plays)
            .Take(take)
            .ToList();
    }

    private static string ToDb(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTime FromDb(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static object Db(object? value) => value ?? DBNull.Value;

    private static void Bind(SqliteCommand cmd, PlaybackHistoryEntry e)
    {
        cmd.Parameters.AddWithValue("@UserId", e.UserId.ToString());
        cmd.Parameters.AddWithValue("@UserName", Db(e.UserName));
        cmd.Parameters.AddWithValue("@ItemId", e.ItemId.ToString());
        cmd.Parameters.AddWithValue("@ItemName", Db(e.ItemName));
        cmd.Parameters.AddWithValue("@ItemType", Db(e.ItemType));
        cmd.Parameters.AddWithValue("@SeriesId", Db(e.SeriesId?.ToString()));
        cmd.Parameters.AddWithValue("@SeriesName", Db(e.SeriesName));
        cmd.Parameters.AddWithValue("@ParentIndexNumber", Db(e.ParentIndexNumber));
        cmd.Parameters.AddWithValue("@IndexNumber", Db(e.IndexNumber));
        cmd.Parameters.AddWithValue("@ProductionYear", Db(e.ProductionYear));
        cmd.Parameters.AddWithValue("@RunTimeTicks", Db(e.RunTimeTicks));
        cmd.Parameters.AddWithValue("@StartedUtc", ToDb(e.StartedUtc));
        cmd.Parameters.AddWithValue("@StoppedUtc", Db(e.StoppedUtc.HasValue ? ToDb(e.StoppedUtc.Value) : null));
        cmd.Parameters.AddWithValue("@WatchedSeconds", e.WatchedSeconds);
        cmd.Parameters.AddWithValue("@PositionTicks", Db(e.PositionTicks));
        cmd.Parameters.AddWithValue("@Completed", e.Completed ? 1 : 0);
        cmd.Parameters.AddWithValue("@Client", Db(e.Client));
        cmd.Parameters.AddWithValue("@DeviceName", Db(e.DeviceName));
        cmd.Parameters.AddWithValue("@RemoteAddress", Db(e.RemoteAddress));
        cmd.Parameters.AddWithValue("@PlayMethod", Db(e.PlayMethod));
        cmd.Parameters.AddWithValue("@TranscodeReasons", Db(e.TranscodeReasons));
        cmd.Parameters.AddWithValue("@VideoCodec", Db(e.VideoCodec));
        cmd.Parameters.AddWithValue("@AudioCodec", Db(e.AudioCodec));
        cmd.Parameters.AddWithValue("@Height", Db(e.Height));
    }

    private static PlaybackHistoryEntry Read(SqliteDataReader r)
    {
        static Guid? G(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i));
        static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static int? I(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        static long? L(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

        return new PlaybackHistoryEntry
        {
            Id = r.GetInt64(0),
            UserId = Guid.Parse(r.GetString(1)),
            UserName = S(r, 2),
            ItemId = Guid.Parse(r.GetString(3)),
            ItemName = S(r, 4),
            ItemType = S(r, 5),
            SeriesId = G(r, 6),
            SeriesName = S(r, 7),
            ParentIndexNumber = I(r, 8),
            IndexNumber = I(r, 9),
            ProductionYear = I(r, 10),
            RunTimeTicks = L(r, 11),
            StartedUtc = FromDb(r.GetString(12)),
            StoppedUtc = r.IsDBNull(13) ? null : FromDb(r.GetString(13)),
            WatchedSeconds = r.GetInt64(14),
            PositionTicks = L(r, 15),
            Completed = r.GetInt64(16) != 0,
            Client = S(r, 17),
            DeviceName = S(r, 18),
            RemoteAddress = S(r, 19),
            PlayMethod = S(r, 20),
            TranscodeReasons = S(r, 21),
            VideoCodec = S(r, 22),
            AudioCodec = S(r, 23),
            Height = I(r, 24)
        };
    }

    private List<PlaybackHistoryEntry> Query(string tail, Action<SqliteCommand> bind)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + Columns + " FROM Plays " + tail;
        bind(cmd);
        using var reader = cmd.ExecuteReader();
        var list = new List<PlaybackHistoryEntry>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }

        return list;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        if (!_initialized)
        {
            lock (_initLock)
            {
                if (!_initialized)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = """
                        PRAGMA journal_mode = WAL;
                        CREATE TABLE IF NOT EXISTS Plays (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            UserId TEXT NOT NULL,
                            UserName TEXT,
                            ItemId TEXT NOT NULL,
                            ItemName TEXT,
                            ItemType TEXT,
                            SeriesId TEXT,
                            SeriesName TEXT,
                            ParentIndexNumber INTEGER,
                            IndexNumber INTEGER,
                            ProductionYear INTEGER,
                            RunTimeTicks INTEGER,
                            StartedUtc TEXT NOT NULL,
                            StoppedUtc TEXT,
                            WatchedSeconds INTEGER NOT NULL DEFAULT 0,
                            PositionTicks INTEGER,
                            Completed INTEGER NOT NULL DEFAULT 0,
                            Client TEXT,
                            DeviceName TEXT,
                            RemoteAddress TEXT,
                            PlayMethod TEXT,
                            TranscodeReasons TEXT,
                            VideoCodec TEXT,
                            AudioCodec TEXT,
                            Height INTEGER
                        );
                        CREATE INDEX IF NOT EXISTS IX_Plays_StartedUtc ON Plays (StartedUtc);
                        CREATE INDEX IF NOT EXISTS IX_Plays_UserId ON Plays (UserId);
                        CREATE INDEX IF NOT EXISTS IX_Plays_SeriesId ON Plays (SeriesId);
                        """;
                    cmd.ExecuteNonQuery();
                    _initialized = true;
                }
            }
        }

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 10000;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }
}
