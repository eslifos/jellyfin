using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.EpisodeOrder;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.EpisodeOrder;
using MediaBrowser.Model.Providers;
using MediaBrowser.Providers.Plugins.Tmdb;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.EpisodeOrder;

/// <summary>
/// Compares a series' episode files with the episode orders TVDB and TMDb have for it.
/// TVDB is read with the TVDB plugin's own project key and subscriber PIN (the plugin applies the orders).
/// </summary>
public sealed class EpisodeOrderService : IEpisodeOrderService, IDisposable
{
    private const string TvdbApi = "https://api4.thetvdb.com/v4/";
    private const string TvdbFallbackKey = "7f7eed88-2530-4f84-8ee7-f154471b8f87";
    private static readonly TimeSpan _cacheTime = TimeSpan.FromHours(6);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IMetadataSourcePreferences _sourcePreferences;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TmdbClientManager _tmdb;
    private readonly ILogger<EpisodeOrderService> _logger;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTime Time, List<OrderData> Orders)> _cache = new();
    private string? _token;
    private DateTime _tokenTime;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeOrderService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="sourcePreferences">The per-series main source.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="tmdb">The TMDb client.</param>
    /// <param name="logger">The logger.</param>
    public EpisodeOrderService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IMetadataSourcePreferences sourcePreferences,
        IHttpClientFactory httpClientFactory,
        TmdbClientManager tmdb,
        ILogger<EpisodeOrderService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _sourcePreferences = sourcePreferences;
        _httpClientFactory = httpClientFactory;
        _tmdb = tmdb;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EpisodeOrderAnalysis> AnalyzeAsync(Series series, bool refresh, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var sources = GetSources();
        var primary = _sourcePreferences.GetPrimarySource(series.Id);
        var effective = primary ?? GetLibrarySource(series, sources);

        var orders = new List<OrderData>();
        var tvdbId = series.GetProviderId(MetadataProvider.Tvdb);
        var tmdbId = series.GetProviderId(MetadataProvider.Tmdb);
        if (string.IsNullOrEmpty(tvdbId) && string.IsNullOrEmpty(tmdbId))
        {
            notes.Add("NoIds");
        }

        if (!string.IsNullOrEmpty(tvdbId) && int.TryParse(tvdbId, CultureInfo.InvariantCulture, out var tvdb))
        {
            if (FindTvdbType("Jellyfin.Plugin.Tvdb.TvdbPlugin") is null)
            {
                notes.Add("TvdbNoPlugin");
            }

            try
            {
                orders.AddRange(await GetCachedAsync("tvdb-" + tvdbId, refresh, () => GetTvdbOrdersAsync(tvdb, cancellationToken)).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Could not read the TVDB orders of {Series}", series.Name);
                notes.Add("TvdbError");
            }
        }

        if (!string.IsNullOrEmpty(tmdbId) && int.TryParse(tmdbId, CultureInfo.InvariantCulture, out var tmdb))
        {
            try
            {
                orders.AddRange(await GetCachedAsync("tmdb-" + tmdbId, refresh, () => GetTmdbOrdersAsync(tmdb, series.GetPreferredMetadataLanguage(), cancellationToken)).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Could not read the TMDb orders of {Series}", series.Name);
                notes.Add("TmdbError");
            }
        }

        var episodes = GetEpisodes(series);
        var current = series.DisplayOrder ?? string.Empty;
        var currentProvider = effective?.Contains("tvdb", StringComparison.OrdinalIgnoreCase) == true ? "Tvdb" : "Tmdb";
        var currentOrder = orders.Find(o => o.Value == current && o.Provider == currentProvider) ?? orders.Find(o => o.Value == current);

        var options = orders.Select(o => new EpisodeOrderOption
        {
            Provider = o.Provider,
            Value = o.Value,
            Name = o.Name,
            AlternateName = o.AlternateName,
            SeasonCount = o.Slots.Keys.Select(k => k.Season).Distinct().Count(),
            EpisodeCount = o.Slots.Count,
            Matched = episodes.Count(e => e.ParentIndexNumber.HasValue && e.IndexNumber.HasValue && o.Slots.ContainsKey((e.ParentIndexNumber.Value, e.IndexNumber.Value))),
            IsCurrent = ReferenceEquals(o, currentOrder)
        }).ToList();

        return new EpisodeOrderAnalysis
        {
            SeriesId = series.Id,
            CurrentOrder = current,
            PrimarySource = primary,
            EffectiveSource = effective,
            Sources = sources,
            Orders = options,
            Notes = notes,
            Episodes = episodes.Select(e =>
            {
                var hasNumber = e.ParentIndexNumber.HasValue && e.IndexNumber.HasValue;
                string? expected = null;
                bool? inOrder = null;
                if (currentOrder is not null && hasNumber)
                {
                    inOrder = currentOrder.Slots.TryGetValue((e.ParentIndexNumber!.Value, e.IndexNumber!.Value), out expected);
                }

                return new EpisodeOrderEpisode
                {
                    Id = e.Id,
                    Name = e.Name ?? string.Empty,
                    FileName = string.IsNullOrEmpty(e.Path) ? null : Path.GetFileName(e.Path),
                    Season = e.ParentIndexNumber,
                    Number = e.IndexNumber,
                    NumberEnd = e.IndexNumberEnd,
                    HasProviderData = HasProviderData(e),
                    InOrder = inOrder,
                    ExpectedName = string.IsNullOrEmpty(expected) ? null : expected
                };
            }).ToList()
        };
    }

    /// <inheritdoc />
    public async Task<(int Fixed, int Remaining)> FixEpisodeNumbersAsync(Series series, CancellationToken cancellationToken)
    {
        int fixedCount = 0, remaining = 0;
        foreach (var episode in GetEpisodes(series).Where(e => !e.IndexNumber.HasValue || !e.ParentIndexNumber.HasValue))
        {
            if (_libraryManager.FillMissingEpisodeNumbersFromPath(episode, true) && episode.IndexNumber.HasValue)
            {
                await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                fixedCount++;
            }
            else
            {
                remaining++;
            }
        }

        return (fixedCount, remaining);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _loginLock.Dispose();
    }

    private static bool HasProviderData(BaseItem item)
        => !string.IsNullOrEmpty(item.GetProviderId(MetadataProvider.Tvdb))
            || !string.IsNullOrEmpty(item.GetProviderId(MetadataProvider.Tmdb))
            || !string.IsNullOrEmpty(item.GetProviderId(MetadataProvider.Imdb));

    private static Type? FindTvdbType(string fullName)
        => AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Jellyfin.Plugin.Tvdb", StringComparison.OrdinalIgnoreCase) == true)
            .Select(a => a.GetType(fullName, false))
            .FirstOrDefault(t => t is not null);

    private List<Episode> GetEpisodes(Series series)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            AncestorIds = [series.Id],
            IncludeItemTypes = [BaseItemKind.Episode],
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(true)
        })
        .OfType<Episode>()
        .OrderBy(e => e.ParentIndexNumber ?? int.MaxValue)
        .ThenBy(e => e.IndexNumber ?? int.MaxValue)
        .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private List<string> GetSources()
        => _providerManager.GetAllMetadataPlugins()
            .FirstOrDefault(p => string.Equals(p.ItemType, nameof(Series), StringComparison.Ordinal))?
            .Plugins.Where(p => p.Type == MetadataPluginType.MetadataFetcher)
            .Select(p => p.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

    private string? GetLibrarySource(Series series, List<string> sources)
    {
        var typeOptions = _libraryManager.GetLibraryOptions(series).GetTypeOptions(nameof(Series));
        var enabled = typeOptions?.MetadataFetchers is { Length: > 0 } f ? f : sources.ToArray();
        var order = typeOptions?.MetadataFetcherOrder ?? [];
        return order.FirstOrDefault(n => enabled.Contains(n, StringComparer.OrdinalIgnoreCase))
            ?? sources.FirstOrDefault(n => enabled.Contains(n, StringComparer.OrdinalIgnoreCase));
    }

    private async Task<List<OrderData>> GetCachedAsync(string key, bool refresh, Func<Task<List<OrderData>>> load)
    {
        if (!refresh && _cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.Time < _cacheTime)
        {
            return hit.Orders;
        }

        var orders = await load().ConfigureAwait(false);
        _cache[key] = (DateTime.UtcNow, orders);
        return orders;
    }

    // ---------------- TVDB ----------------

    private async Task<List<OrderData>> GetTvdbOrdersAsync(int tvdbId, CancellationToken cancellationToken)
    {
        using var extended = await TvdbGetAsync($"series/{tvdbId.ToString(CultureInfo.InvariantCulture)}/extended?short=true", cancellationToken).ConfigureAwait(false);
        var data = extended.RootElement.GetProperty("data");

        // Season types of this series, with TVDB's names
        var types = new Dictionary<string, (string Name, string? Alt)>(StringComparer.OrdinalIgnoreCase);
        void AddType(JsonElement t)
        {
            var type = Str(t, "type");
            if (!string.IsNullOrEmpty(type) && !types.ContainsKey(type))
            {
                types[type] = (Str(t, "name") ?? type, Str(t, "alternateName"));
            }
        }

        if (data.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in seasons.EnumerateArray())
            {
                if (s.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Object)
                {
                    AddType(t);
                }
            }
        }

        if (types.Count == 0)
        {
            types["official"] = ("Aired Order", null);
        }

        var result = new List<OrderData>();
        foreach (var (type, names) in types)
        {
            var slots = new Dictionary<(int Season, int Number), string?>();
            string? next = $"series/{tvdbId.ToString(CultureInfo.InvariantCulture)}/episodes/{Uri.EscapeDataString(type)}?page=0";
            for (var page = 0; next is not null && page < 20; page++)
            {
                using var doc = await TvdbGetAsync(next, cancellationToken).ConfigureAwait(false);
                if (doc.RootElement.GetProperty("data").TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in eps.EnumerateArray())
                    {
                        if (Int(e, "seasonNumber") is int sn && Int(e, "number") is int n)
                        {
                            slots.TryAdd((sn, n), Str(e, "name"));
                        }
                    }
                }

                next = doc.RootElement.TryGetProperty("links", out var links) ? Str(links, "next") : null;
                if (next is not null)
                {
                    // "links.next" is an absolute URL
                    next = next.StartsWith(TvdbApi, StringComparison.OrdinalIgnoreCase) ? next[TvdbApi.Length..] : null;
                }
            }

            result.Add(new OrderData
            {
                Provider = "Tvdb",
                Value = string.Equals(type, "official", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "default", StringComparison.OrdinalIgnoreCase) ? string.Empty : type,
                Name = names.Name,
                AlternateName = string.IsNullOrEmpty(names.Alt) || names.Alt == names.Name ? null : names.Alt,
                Slots = slots
            });
        }

        return result;
    }

    private async Task<JsonDocument> TvdbGetAsync(string path, CancellationToken cancellationToken)
    {
        var token = await GetTvdbTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, TvdbApi + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClientFactory.CreateClient(NamedClient.Default).SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _token = null;
        }

        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> GetTvdbTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && DateTime.UtcNow - _tokenTime < TimeSpan.FromDays(20))
        {
            return _token;
        }

        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTime.UtcNow - _tokenTime < TimeSpan.FromDays(20))
            {
                return _token;
            }

            var (key, pin) = GetTvdbCredentials();
            object body = string.IsNullOrEmpty(pin) ? new { apikey = key } : new { apikey = key, pin };
            using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .PostAsJsonAsync(TvdbApi + "login", body, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            _token = Str(doc.RootElement.GetProperty("data"), "token") ?? throw new InvalidOperationException("TVDB login returned no token");
            _tokenTime = DateTime.UtcNow;
            return _token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>
    /// Project key and subscriber PIN of the installed TVDB plugin (read by reflection: it is a separate assembly).
    /// </summary>
    private static (string Key, string? Pin) GetTvdbCredentials()
    {
        var key = FindTvdbType("Jellyfin.Plugin.Tvdb.Configuration.PluginConfiguration")?
            .GetField("ProjectApiKey", BindingFlags.Public | BindingFlags.Static)?
            .GetRawConstantValue() as string;

        string? pin = null;
        try
        {
            var instance = FindTvdbType("Jellyfin.Plugin.Tvdb.TvdbPlugin")?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var config = instance?.GetType().GetProperty("Configuration")?.GetValue(instance);
            pin = config?.GetType().GetProperty("SubscriberPIN")?.GetValue(config) as string;
        }
        catch (TargetInvocationException)
        {
            // No PIN: the project key works alone
        }

        return (string.IsNullOrEmpty(key) ? TvdbFallbackKey : key, pin);
    }

    // ---------------- TMDb ----------------

    private async Task<List<OrderData>> GetTmdbOrdersAsync(int tmdbId, string? language, CancellationToken cancellationToken)
    {
        var result = new List<OrderData>();
        var show = await _tmdb.GetSeriesAsync(tmdbId, language, null, null, cancellationToken).ConfigureAwait(false);
        if (show is null)
        {
            return result;
        }

        var aired = new Dictionary<(int Season, int Number), string?>();
        foreach (var season in show.Seasons ?? [])
        {
            for (var n = 1; n <= season.EpisodeCount; n++)
            {
                aired.TryAdd((season.SeasonNumber, n), null);
            }
        }

        result.Add(new OrderData { Provider = "Tmdb", Value = string.Empty, Name = "Aired Order", Slots = aired });

        foreach (var (displayOrder, group) in await _tmdb.GetSeriesEpisodeGroupsAsync(tmdbId, language, cancellationToken).ConfigureAwait(false))
        {
            var slots = new Dictionary<(int Season, int Number), string?>();
            foreach (var g in group.Groups ?? [])
            {
                foreach (var e in g.Episodes ?? [])
                {
                    // Episode order inside a group starts at 0
                    slots.TryAdd((g.Order, (int)e.Order + 1), e.Name);
                }
            }

            result.Add(new OrderData { Provider = "Tmdb", Value = displayOrder, Name = group.Name ?? displayOrder, Slots = slots });
        }

        return result;
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private sealed class OrderData
    {
        public string Provider { get; init; } = string.Empty;

        public string Value { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string? AlternateName { get; init; }

        public Dictionary<(int Season, int Number), string?> Slots { get; init; } = [];
    }
}
