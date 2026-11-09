using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Imdb;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Imdb;

/// <summary>
/// IMDb ratings from the official datasets (https://datasets.imdbws.com, personal and non-commercial use):
/// title.ratings for every title and title.episode for the episodes of the library's series, kept in
/// imdb-ratings.db in the data folder.
/// </summary>
public sealed class ImdbRatingsService : IImdbRatingsService, IDisposable
{
    private const string RatingsUrl = "https://datasets.imdbws.com/title.ratings.tsv.gz";
    private const string EpisodesUrl = "https://datasets.imdbws.com/title.episode.tsv.gz";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ImdbRatingsService> _logger;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private string _connectionString;
    private DateTime? _lastUpdated;
    private bool _checked;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImdbRatingsService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public ImdbRatingsService(IHttpClientFactory httpClientFactory, ILibraryManager libraryManager, IApplicationPaths appPaths, ILogger<ImdbRatingsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
        _dbPath = Path.Combine(appPaths.DataPath, "imdb-ratings.db");
        _connectionString = BuildConnectionString(_dbPath, SqliteOpenMode.ReadOnly);
    }

    /// <inheritdoc />
    public DateTime? LastUpdated
    {
        get
        {
            EnsureChecked();
            return _lastUpdated;
        }
    }

    /// <inheritdoc />
    public ImdbRating? GetRating(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (LastUpdated is null)
        {
            return null;
        }

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var own = ParseId(item.GetProviderId(MetadataProvider.Imdb));
            if (own > 0)
            {
                var rating = Lookup(connection, own);
                if (rating is not null)
                {
                    return rating;
                }
            }

            // Episodes without their own id: series id + season + episode number
            if (item is Episode episode && episode.ParentIndexNumber is int season && episode.IndexNumber is int number)
            {
                var series = ParseId(episode.Series?.GetProviderId(MetadataProvider.Imdb));
                if (series > 0)
                {
                    var tconst = FindEpisode(connection, series, season, number);
                    if (tconst > 0)
                    {
                        return Lookup(connection, tconst);
                    }
                }
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogDebug(ex, "IMDb lookup failed for {Item}", item.Name);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temp = _dbPath + ".new";
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            // Series of the library: only their episodes are kept from title.episode
            var seriesIds = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series], Recursive = true })
                .Select(s => ParseId(s.GetProviderId(MetadataProvider.Imdb)))
                .Where(id => id > 0)
                .ToHashSet();

            var writeString = BuildConnectionString(temp, SqliteOpenMode.ReadWriteCreate);
            await using (var connection = new SqliteConnection(writeString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                Execute(connection, "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF;");
                Execute(connection, "CREATE TABLE Ratings (Id INTEGER PRIMARY KEY, Rating REAL NOT NULL, Votes INTEGER NOT NULL);");
                Execute(connection, "CREATE TABLE Episodes (Parent INTEGER NOT NULL, Season INTEGER NOT NULL, Episode INTEGER NOT NULL, Id INTEGER NOT NULL, PRIMARY KEY (Parent, Season, Episode)) WITHOUT ROWID;");
                Execute(connection, "CREATE TABLE Meta (Key TEXT PRIMARY KEY, Value TEXT);");

                var ratings = await ImportAsync(connection, RatingsUrl, "INSERT OR REPLACE INTO Ratings VALUES (@a, @b, @c)", ParseRating, new Progress<double>(p => progress.Report(p * 0.6)), cancellationToken).ConfigureAwait(false);
                var episodes = await ImportAsync(connection, EpisodesUrl, "INSERT OR REPLACE INTO Episodes VALUES (@a, @b, @c, @d)", cols => ParseEpisode(cols, seriesIds), new Progress<double>(p => progress.Report(60 + (p * 0.3))), cancellationToken).ConfigureAwait(false);

                using var meta = connection.CreateCommand();
                meta.CommandText = "INSERT INTO Meta VALUES ('Updated', @d)";
                meta.Parameters.AddWithValue("@d", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await meta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("IMDb datasets imported: {Ratings} ratings, {Episodes} episodes of {Series} series", ratings, episodes, seriesIds.Count);
            }

            SqliteConnection.ClearAllPools();
            File.Move(temp, _dbPath, true);
            _checked = false;
            EnsureChecked();

            await FillEpisodeIdsAsync(new Progress<double>(p => progress.Report(90 + (p * 0.1))), cancellationToken).ConfigureAwait(false);
            progress.Report(100);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private static string BuildConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Cache = SqliteCacheMode.Private,
        Pooling = mode == SqliteOpenMode.ReadOnly
    }.ToString();

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>"tt0111161" → 111161.</summary>
    private static int ParseId(string? imdbId)
    {
        if (string.IsNullOrEmpty(imdbId) || imdbId.Length < 3 || !imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return int.TryParse(imdbId.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }

    private static object[]? ParseRating(string[] cols)
    {
        if (cols.Length < 3)
        {
            return null;
        }

        var id = ParseId(cols[0]);
        if (id <= 0)
        {
            return null;
        }

        if (!double.TryParse(cols[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rating)
            || !int.TryParse(cols[2], NumberStyles.None, CultureInfo.InvariantCulture, out var votes))
        {
            return null;
        }

        return new object[] { id, rating, votes };
    }

    private static object[]? ParseEpisode(string[] cols, HashSet<int> seriesIds)
    {
        if (cols.Length < 4)
        {
            return null;
        }

        var parent = ParseId(cols[1]);
        if (!seriesIds.Contains(parent)
            || !int.TryParse(cols[2], NumberStyles.None, CultureInfo.InvariantCulture, out var season)
            || !int.TryParse(cols[3], NumberStyles.None, CultureInfo.InvariantCulture, out var episode))
        {
            return null;
        }

        var id = ParseId(cols[0]);
        return id > 0 ? new object[] { parent, season, episode, id } : null;
    }

    private static ImdbRating? Lookup(SqliteConnection connection, int id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Rating, Votes FROM Ratings WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ImdbRating
        {
            ImdbId = "tt" + id.ToString("D7", CultureInfo.InvariantCulture),
            Rating = (float)reader.GetDouble(0),
            Votes = reader.GetInt32(1)
        };
    }

    private static int FindEpisode(SqliteConnection connection, int series, int season, int episode)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Episodes WHERE Parent = @p AND Season = @s AND Episode = @e";
        cmd.Parameters.AddWithValue("@p", series);
        cmd.Parameters.AddWithValue("@s", season);
        cmd.Parameters.AddWithValue("@e", episode);
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 0;
    }

    private void EnsureChecked()
    {
        if (_checked)
        {
            return;
        }

        _checked = true;
        _lastUpdated = null;
        if (!File.Exists(_dbPath))
        {
            return;
        }

        try
        {
            _connectionString = BuildConnectionString(_dbPath, SqliteOpenMode.ReadOnly);
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Meta WHERE Key = 'Updated'";
            if (cmd.ExecuteScalar() is string value && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
            {
                _lastUpdated = date;
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "The IMDb database {Path} could not be read", _dbPath);
        }
    }

    private async Task<int> ImportAsync(
        SqliteConnection connection,
        string url,
        string insertSql,
        Func<string[], object[]?> parse,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = TimeSpan.FromMinutes(10);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;

        var raw = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (raw.ConfigureAwait(false))
        {
            var counting = new CountingStream(raw);
            return await Task.Run(() => ImportRows(connection, counting, insertSql, parse, total, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    private static int ImportRows(
        SqliteConnection connection,
        CountingStream counting,
        string insertSql,
        Func<string[], object[]?> parse,
        long total,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        {
            using var gzip = new GZipStream(counting, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);

            using var transaction = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = insertSql;
            var names = new[] { "@a", "@b", "@c", "@d" };
            var parameterCount = insertSql.Count(c => c == '@');
            for (var i = 0; i < parameterCount; i++)
            {
                cmd.Parameters.Add(new SqliteParameter(names[i], null));
            }

            cmd.Prepare();

            var rows = 0;
            reader.ReadLine(); // header
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = parse(line.Split('\t'));
                if (values is null)
                {
                    continue;
                }

                for (var i = 0; i < parameterCount; i++)
                {
                    cmd.Parameters[i].Value = values[i];
                }

                cmd.ExecuteNonQuery();
                if (++rows % 50000 == 0 && total > 0)
                {
                    progress.Report(100.0 * counting.BytesRead / total);
                }
            }

            transaction.Commit();
            progress.Report(100);
            return rows;
        }
    }

    /// <summary>
    /// Writes the IMDb id into the episodes of the library that do not have one (found by series + season + episode).
    /// </summary>
    private async Task FillEpisodeIdsAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false
        })
            .OfType<Episode>()
            .Where(e => !e.HasProviderId(MetadataProvider.Imdb) && e.ParentIndexNumber.HasValue && e.IndexNumber.HasValue)
            .ToList();

        if (episodes.Count == 0)
        {
            return;
        }

        var connection = new SqliteConnection(_connectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var filled = 0;
        for (var i = 0; i < episodes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episode = episodes[i];
            var series = ParseId(episode.Series?.GetProviderId(MetadataProvider.Imdb));
            if (series > 0)
            {
                var tconst = FindEpisode(connection, series, episode.ParentIndexNumber!.Value, episode.IndexNumber!.Value);
                if (tconst > 0)
                {
                    episode.SetProviderId(MetadataProvider.Imdb, "tt" + tconst.ToString("D7", CultureInfo.InvariantCulture));
                    await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    filled++;

                    // Small pause so library writes from the scan are not starved
                    if (filled % 50 == 0)
                    {
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            progress.Report(100.0 * (i + 1) / episodes.Count);
        }

        _logger.LogInformation("IMDb ids added to {Count} episodes", filled);
    }

    /// <inheritdoc />
    public void Dispose() => _updateLock.Dispose();

    /// <summary>Counts the compressed bytes read, for the progress.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;

        public CountingStream(Stream inner) => _inner = inner;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
