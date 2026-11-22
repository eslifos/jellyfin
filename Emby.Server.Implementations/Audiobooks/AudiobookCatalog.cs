using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Audiobooks;

/// <summary>
/// Looks audiobooks up in free online catalogs, Spanish first:
/// Audible Spain, Audible US, Apple Books (Spain, US), Google Books (Spanish) and Open Library.
/// The best match gives the main data; the other catalogs fill what it lacks (synopsis, cover, ISBN…).
/// </summary>
public sealed partial class AudiobookCatalog
{
    internal const double MinScore = 0.6;

    private static readonly TimeSpan _cacheAge = TimeSpan.FromHours(12);
    private static readonly ConcurrentDictionary<string, (DateTime Built, List<AudiobookMatch> Matches)> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Audible stores: the US one (its Spanish catalog is Latin American) and Spain. There is no Mexican store.
    /// Both are searched and the edition closest to the wanted Spanish wins.
    /// </summary>
    private static readonly (string Region, string Host)[] _audibleRegions =
    [
        ("us", "https://api.audible.com"),
        ("es", "https://api.audible.es")
    ];

    /// <summary>Apple Books stores with audiobooks (the Mexican store has none in the public search).</summary>
    private static readonly string[] _appleCountries = ["us", "es"];

    private const string AudibleGroups = "contributors,product_attrs,product_desc,product_extended_attrs,series,media,rating,category_ladders";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudiobookCatalog"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public AudiobookCatalog(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Finds the book in the catalogs and merges what they know about it.
    /// </summary>
    /// <param name="query">Title and author to look up.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The merged match, or null when no catalog has it.</returns>
    public async Task<AudiobookMatch?> FindAsync(AudiobookQuery query, CancellationToken cancellationToken)
    {
        // Stores list a saga book by its own title ("El club de la luna llena", not "Anna Kadabra 1. El club…")
        // and often without the subtitle: shorter titles are tried when the full one finds nothing with a cover
        AudiobookMatch? fallback = null;
        foreach (var title in TitleVariants(query.Title))
        {
            var attempt = string.Equals(title, query.Title, StringComparison.Ordinal) ? query : query.WithTitle(title);
            var match = await FindOnceAsync(attempt, cancellationToken).ConfigureAwait(false);
            if (match is not null && !string.IsNullOrEmpty(match.ImageUrl))
            {
                return match;
            }

            fallback ??= match;
        }

        return fallback;
    }

    /// <summary>
    /// The title as it is, then without a saga and number in front ("Saga 3. Title"), then without the
    /// subtitle (after ":" or " - ").
    /// </summary>
    /// <param name="title">The clean title.</param>
    /// <returns>Titles to try, in order.</returns>
    internal static List<string> TitleVariants(string title)
    {
        var variants = new List<string> { title };
        void Add(string? candidate)
        {
            candidate = candidate?.Trim(' ', '-', '–', ':', ',');
            if (!string.IsNullOrEmpty(candidate) && candidate.Length >= 3 && !variants.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                variants.Add(candidate);
            }
        }

        var saga = SagaPrefix().Match(title);
        var withoutSaga = saga.Success ? saga.Groups["title"].Value : null;
        Add(withoutSaga);

        foreach (var text in new[] { withoutSaga, title })
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var cut = text.IndexOfAny([':', ';']);
            if (cut < 0)
            {
                cut = text.IndexOf(" - ", StringComparison.Ordinal);
            }

            if (cut > 0)
            {
                Add(text[..cut]);
            }
        }

        return variants;
    }

    private async Task<AudiobookMatch?> FindOnceAsync(AudiobookQuery query, CancellationToken cancellationToken)
    {
        var found = new List<AudiobookMatch>();

        // Audible first: both stores, the edition in the wanted Spanish wins (Latin American, otherwise Spain)
        var audible = new List<AudiobookMatch>();
        foreach (var (region, host) in _audibleRegions)
        {
            audible.AddRange(await SearchAudibleAsync(host, region, query, cancellationToken).ConfigureAwait(false));
        }

        if (Best(audible) is { } hit)
        {
            found.Add(hit);
        }
        else
        {
            // Apple Books when Audible doesn't have it
            var apple = new List<AudiobookMatch>();
            foreach (var country in _appleCountries)
            {
                apple.AddRange(await SearchAppleAsync(country, query, cancellationToken).ConfigureAwait(false));
            }

            if (Best(apple) is { } appleHit)
            {
                found.Add(appleHit);
            }
        }

        // Google Books for a Spanish synopsis, ISBN and publisher when missing
        if (found.Count == 0 || string.IsNullOrEmpty(found[0].Overview) || string.IsNullOrEmpty(found[0].Publisher))
        {
            if (Best(await SearchGoogleAsync(query, cancellationToken).ConfigureAwait(false)) is { } google)
            {
                found.Add(google);
            }
        }

        // Open Library only for a cover
        if (found.All(f => string.IsNullOrEmpty(f.ImageUrl)))
        {
            if (Best(await SearchOpenLibraryAsync(query, cancellationToken).ConfigureAwait(false)) is { } openLibrary)
            {
                found.Add(openLibrary);
            }
        }

        return Merge(found);
    }

    /// <summary>
    /// Best match of every catalog (for covers to choose from and the "Identify" results).
    /// </summary>
    /// <param name="query">Title and author to look up.</param>
    /// <param name="perSource">How many results of each catalog.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matches, best first within each catalog.</returns>
    public async Task<List<AudiobookMatch>> SearchAllAsync(AudiobookQuery query, int perSource, CancellationToken cancellationToken)
    {
        var lists = new List<List<AudiobookMatch>>();
        foreach (var (region, host) in _audibleRegions)
        {
            lists.Add(await SearchAudibleAsync(host, region, query, cancellationToken).ConfigureAwait(false));
        }

        foreach (var country in _appleCountries)
        {
            lists.Add(await SearchAppleAsync(country, query, cancellationToken).ConfigureAwait(false));
        }
        lists.Add(await SearchGoogleAsync(query, cancellationToken).ConfigureAwait(false));
        lists.Add(await SearchOpenLibraryAsync(query, cancellationToken).ConfigureAwait(false));

        // Without a title match the results are only useful when nothing else was found
        var good = lists.SelectMany(l => l.Where(m => m.Score >= MinScore).OrderByDescending(m => m.Score).Take(perSource)).ToList();
        return good.Count > 0 ? good : lists.SelectMany(l => l.OrderByDescending(m => m.Score).Take(perSource)).ToList();
    }

    /// <summary>
    /// Gets a book by its id in one catalog (after choosing it in "Identify").
    /// </summary>
    /// <param name="source">The catalog.</param>
    /// <param name="id">The id in that catalog.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The book, or null.</returns>
    public async Task<AudiobookMatch?> GetByIdAsync(string source, string id, CancellationToken cancellationToken)
    {
        switch (source)
        {
            case AudiobookSources.Audible:
                foreach (var (region, host) in _audibleRegions)
                {
                    var url = $"{host}/1.0/catalog/products/{Uri.EscapeDataString(id)}?response_groups={AudibleGroups}&image_sizes=500,1024,2400";
                    using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
                    if (doc is not null && doc.RootElement.TryGetProperty("product", out var product) && Str(product, "title") is not null)
                    {
                        var match = ParseAudible(product, region);
                        match.Score = 1;
                        return match;
                    }
                }

                return null;
            case AudiobookSources.AppleBooks:
                foreach (var country in _appleCountries)
                {
                    using var doc = await GetJsonAsync($"https://itunes.apple.com/lookup?id={Uri.EscapeDataString(id)}&country={country}", cancellationToken).ConfigureAwait(false);
                    var first = doc is not null && doc.RootElement.TryGetProperty("results", out var results) ? results.EnumerateArray().FirstOrDefault() : default;
                    if (first.ValueKind == JsonValueKind.Object)
                    {
                        return WithScore(ParseApple(first, country), 1);
                    }
                }

                return null;

            case AudiobookSources.GoogleBooks:
            {
                using var doc = await GetJsonAsync($"https://www.googleapis.com/books/v1/volumes/{Uri.EscapeDataString(id)}", cancellationToken).ConfigureAwait(false);
                return doc is not null && doc.RootElement.TryGetProperty("volumeInfo", out _) ? WithScore(ParseGoogle(doc.RootElement), 1) : null;
            }

            default:
                return null;
        }
    }

    private static AudiobookMatch WithScore(AudiobookMatch match, double score)
    {
        match.Score = score;
        return match;
    }

    private static AudiobookMatch? Best(List<AudiobookMatch> matches)
        => matches.Where(m => m.Score >= MinScore).OrderByDescending(m => m.Score).FirstOrDefault();

    /// <summary>The first match gives the data; the others fill the gaps.</summary>
    private static AudiobookMatch? Merge(List<AudiobookMatch> found)
    {
        if (found.Count == 0)
        {
            return null;
        }

        var main = found[0];
        foreach (var other in found.Skip(1))
        {
            main.Overview ??= other.Overview;
            main.Publisher ??= other.Publisher;
            main.ReleaseDate ??= other.ReleaseDate;
            main.Isbn ??= other.Isbn;
            main.SeriesName ??= other.SeriesName;
            main.SeriesIndex ??= other.SeriesIndex;
            main.Rating ??= other.Rating;
            main.Subtitle ??= other.Subtitle;
            if (main.Genres.Count == 0)
            {
                main.Genres = other.Genres;
            }

            if (main.Authors.Count == 0)
            {
                main.Authors = other.Authors;
            }

            if (string.IsNullOrEmpty(main.ImageUrl))
            {
                main.ImageUrl = other.ImageUrl;
                main.ImageSize = other.ImageSize;
            }
        }

        return main;
    }

    // ----- Audible -----

    private Task<List<AudiobookMatch>> SearchAudibleAsync(string host, string region, AudiobookQuery query, CancellationToken cancellationToken)
        => CachedAsync(
            AudiobookSources.Audible + region,
            query,
            async () =>
            {
                var url = new StringBuilder(host)
                    .Append("/1.0/catalog/products?num_results=10&products_sort_by=Relevance&response_groups=").Append(AudibleGroups)
                    .Append("&image_sizes=500,1024,2400&title=").Append(Uri.EscapeDataString(query.Title));
                if (!string.IsNullOrEmpty(query.Author))
                {
                    url.Append("&author=").Append(Uri.EscapeDataString(query.Author));
                }

                var found = await ReadAudibleAsync(url.ToString(), region, cancellationToken).ConfigureAwait(false);
                if (found.Count > 0)
                {
                    return found;
                }

                // The title search is strict (an extra word finds nothing): try the words of title and author
                var keywords = string.IsNullOrEmpty(query.Author) ? query.Title : query.Title + " " + query.Author;
                var keywordUrl = new StringBuilder(host)
                    .Append("/1.0/catalog/products?num_results=10&products_sort_by=Relevance&response_groups=").Append(AudibleGroups)
                    .Append("&image_sizes=500,1024,2400&keywords=").Append(Uri.EscapeDataString(keywords));
                return await ReadAudibleAsync(keywordUrl.ToString(), region, cancellationToken).ConfigureAwait(false);
            });

    private async Task<List<AudiobookMatch>> ReadAudibleAsync(string url, string region, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return products.EnumerateArray().Select(p => ParseAudible(p, region)).ToList();
    }

    private static AudiobookMatch ParseAudible(JsonElement p, string region)
    {
        var match = new AudiobookMatch
        {
            Source = AudiobookSources.Audible,
            Id = Str(p, "asin") ?? string.Empty,
            Title = Str(p, "title") ?? string.Empty,
            Subtitle = Str(p, "subtitle"),
            Authors = Names(p, "authors"),
            Narrators = Names(p, "narrators"),
            Publisher = Str(p, "publisher_name"),
            ReleaseDate = Date(Str(p, "release_date") ?? Str(p, "issue_date")),
            Overview = PlainText(Str(p, "publisher_summary") ?? Str(p, "merchandising_summary")),
            Language = Str(p, "language") ?? region,
            Region = region,
            RuntimeMinutes = p.TryGetProperty("runtime_length_min", out var runtime) && runtime.TryGetInt32(out var minutes) ? minutes : null
        };

        if (p.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Array && series.GetArrayLength() > 0)
        {
            var first = series[0];
            match.SeriesName = Str(first, "title");
            match.SeriesIndex = ParseIndex(Str(first, "sequence"));
        }

        if (p.TryGetProperty("product_images", out var images) && images.ValueKind == JsonValueKind.Object)
        {
            var best = images.EnumerateObject()
                .Select(i => (Size: int.TryParse(i.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0, Url: i.Value.GetString()))
                .Where(i => !string.IsNullOrEmpty(i.Url))
                .OrderByDescending(i => i.Size)
                .FirstOrDefault();
            match.ImageUrl = best.Url;
            match.ImageSize = best.Size > 0 ? best.Size : null;
        }

        if (p.TryGetProperty("rating", out var rating)
            && rating.TryGetProperty("overall_distribution", out var overall)
            && overall.TryGetProperty("display_average_rating", out var average))
        {
            var value = average.ValueKind == JsonValueKind.Number ? average.GetDouble()
                : double.TryParse(average.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            match.Rating = value > 0 ? (float)Math.Round(value * 2, 1) : null;
        }

        if (p.TryGetProperty("category_ladders", out var ladders) && ladders.ValueKind == JsonValueKind.Array)
        {
            match.Genres = ladders.EnumerateArray()
                .SelectMany(l => l.TryGetProperty("ladder", out var ladder) && ladder.ValueKind == JsonValueKind.Array ? ladder.EnumerateArray() : Enumerable.Empty<JsonElement>())
                .Select(c => Str(c, "name"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
        }

        return match;
    }

    // ----- Apple Books -----

    private Task<List<AudiobookMatch>> SearchAppleAsync(string country, AudiobookQuery query, CancellationToken cancellationToken)
        => CachedAsync(
            AudiobookSources.AppleBooks + country,
            query,
            async () =>
            {
                var term = string.IsNullOrEmpty(query.Author) ? query.Title : query.Title + " " + query.Author;
                var url = $"https://itunes.apple.com/search?media=audiobook&entity=audiobook&country={country}&limit=10&term={Uri.EscapeDataString(term)}";
                using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
                if (doc is null || !doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                return results.EnumerateArray().Select(r => ParseApple(r, country)).ToList();
            });

    private static AudiobookMatch ParseApple(JsonElement r, string country)
    {
        var artwork = Str(r, "artworkUrl100") ?? Str(r, "artworkUrl60");
        var id = r.TryGetProperty("collectionId", out var collectionId) && collectionId.TryGetInt64(out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var genre = Str(r, "primaryGenreName");
        return new AudiobookMatch
        {
            Source = AudiobookSources.AppleBooks,
            Id = id,
            Title = Str(r, "collectionName") ?? Str(r, "trackName") ?? string.Empty,
            Authors = SplitNames(Str(r, "artistName")),
            ReleaseDate = Date(Str(r, "releaseDate")),
            Overview = PlainText(Str(r, "description")),
            Genres = string.IsNullOrEmpty(genre) ? [] : [genre],
            ImageUrl = artwork is null ? null : ArtworkSize().Replace(artwork, "1400x1400bb"),
            ImageSize = artwork is null ? null : 1400,
            Language = country == "es" ? "es" : null,
            Region = country
        };
    }

    // ----- Google Books -----

    private Task<List<AudiobookMatch>> SearchGoogleAsync(AudiobookQuery query, CancellationToken cancellationToken)
        => CachedAsync(
            AudiobookSources.GoogleBooks,
            query,
            async () =>
            {
                var q = "intitle:" + query.Title + (string.IsNullOrEmpty(query.Author) ? string.Empty : " inauthor:" + query.Author);
                var url = $"https://www.googleapis.com/books/v1/volumes?maxResults=10&printType=books&langRestrict=es&q={Uri.EscapeDataString(q)}";
                using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
                if (doc is null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                return items.EnumerateArray().Select(ParseGoogle).ToList();
            });

    private static AudiobookMatch ParseGoogle(JsonElement item)
    {
        var info = item.GetProperty("volumeInfo");
        string? image = null;
        if (info.TryGetProperty("imageLinks", out var links))
        {
            image = Str(links, "extraLarge") ?? Str(links, "large") ?? Str(links, "medium") ?? Str(links, "small") ?? Str(links, "thumbnail");
            image = image?.Replace("http://", "https://", StringComparison.Ordinal).Replace("&edge=curl", string.Empty, StringComparison.Ordinal);
        }

        string? isbn = null;
        if (info.TryGetProperty("industryIdentifiers", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            var list = ids.EnumerateArray().Select(i => (Type: Str(i, "type"), Id: Str(i, "identifier"))).ToList();
            isbn = list.FirstOrDefault(i => i.Type == "ISBN_13").Id ?? list.FirstOrDefault(i => i.Type == "ISBN_10").Id;
        }

        return new AudiobookMatch
        {
            Source = AudiobookSources.GoogleBooks,
            Id = Str(item, "id") ?? string.Empty,
            Title = Str(info, "title") ?? string.Empty,
            Subtitle = Str(info, "subtitle"),
            Authors = Strings(info, "authors"),
            Publisher = Str(info, "publisher"),
            ReleaseDate = Date(Str(info, "publishedDate")),
            Overview = PlainText(Str(info, "description")),
            Genres = Strings(info, "categories"),
            ImageUrl = image,
            Rating = info.TryGetProperty("averageRating", out var rating) && rating.TryGetDouble(out var value) ? (float)Math.Round(value * 2, 1) : null,
            Language = Str(info, "language"),
            Isbn = isbn
        };
    }

    // ----- Open Library -----

    private Task<List<AudiobookMatch>> SearchOpenLibraryAsync(AudiobookQuery query, CancellationToken cancellationToken)
        => CachedAsync(
            AudiobookSources.OpenLibrary,
            query,
            async () =>
            {
                var url = new StringBuilder("https://openlibrary.org/search.json?limit=5&fields=key,title,author_name,first_publish_year,cover_i,isbn,publisher&title=")
                    .Append(Uri.EscapeDataString(query.Title));
                if (!string.IsNullOrEmpty(query.Author))
                {
                    url.Append("&author=").Append(Uri.EscapeDataString(query.Author));
                }

                using var doc = await GetJsonAsync(url.ToString(), cancellationToken).ConfigureAwait(false);
                if (doc is null || !doc.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                return docs.EnumerateArray().Select(d =>
                {
                    var cover = d.TryGetProperty("cover_i", out var coverId) && coverId.TryGetInt64(out var c) ? c : 0;
                    var year = d.TryGetProperty("first_publish_year", out var y) && y.TryGetInt32(out var yy) ? yy : 0;
                    return new AudiobookMatch
                    {
                        Source = AudiobookSources.OpenLibrary,
                        Id = (Str(d, "key") ?? string.Empty).Replace("/works/", string.Empty, StringComparison.Ordinal),
                        Title = Str(d, "title") ?? string.Empty,
                        Authors = Strings(d, "author_name"),
                        Publisher = Strings(d, "publisher").FirstOrDefault(),
                        ReleaseDate = year > 0 ? new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
                        ImageUrl = cover > 0 ? $"https://covers.openlibrary.org/b/id/{cover.ToString(CultureInfo.InvariantCulture)}-L.jpg" : null,
                        Isbn = Strings(d, "isbn").FirstOrDefault()
                    };
                }).ToList();
            });

    // ----- Shared -----

    private async Task<List<AudiobookMatch>> CachedAsync(string source, AudiobookQuery query, Func<Task<List<AudiobookMatch>>> search)
    {
        var key = source + "|" + query.Variant + "|" + Normalize(query.Title) + "|" + Normalize(query.Author ?? string.Empty);
        if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Built < _cacheAge)
        {
            return cached.Matches.Select(Clone).ToList();
        }

        List<AudiobookMatch> matches;
        try
        {
            matches = await search().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            _logger.LogDebug(ex, "Audiobook search in {Source} failed", source);
            return [];
        }

        foreach (var match in matches)
        {
            match.Score = Score(match, query);
        }

        _cache[key] = (DateTime.UtcNow, matches);
        return matches.Select(Clone).ToList();
    }

    private static AudiobookMatch Clone(AudiobookMatch m) => new()
    {
        Source = m.Source,
        Id = m.Id,
        Title = m.Title,
        Subtitle = m.Subtitle,
        Authors = m.Authors,
        Narrators = m.Narrators,
        Publisher = m.Publisher,
        ReleaseDate = m.ReleaseDate,
        Overview = m.Overview,
        Genres = m.Genres,
        SeriesName = m.SeriesName,
        SeriesIndex = m.SeriesIndex,
        ImageUrl = m.ImageUrl,
        ImageSize = m.ImageSize,
        Rating = m.Rating,
        Language = m.Language,
        Region = m.Region,
        Isbn = m.Isbn,
        RuntimeMinutes = m.RuntimeMinutes,
        Score = m.Score
    };

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Audiobook catalog answered {Status} for {Url}", (int)response.StatusCode, url);
            return null;
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>How well a result matches: the title counts most, the author confirms it, Spanish editions win ties.</summary>
    private static double Score(AudiobookMatch match, AudiobookQuery query)
    {
        var title = TitleSimilarity(query.Title, match.Title);
        if (!string.IsNullOrEmpty(match.Subtitle))
        {
            title = Math.Max(title, TitleSimilarity(query.Title, match.Title + " " + match.Subtitle));
        }

        var score = title * 0.8;
        if (!string.IsNullOrEmpty(query.Author))
        {
            var wanted = Tokens(query.Author);
            var authorHit = match.Authors.Any(a => Tokens(a).Intersect(wanted).Any());
            score += authorHit ? 0.2 : match.Authors.Count > 0 ? -0.2 : 0;
        }
        else
        {
            score += 0.1;
        }

        score += SpanishVariant.Bonus(match, query.Variant);

        if (query.Year is int year && match.ReleaseDate is DateTime date && Math.Abs(date.Year - year) <= 1)
        {
            score += 0.05;
        }

        // Not capped at 1: two editions with the same title are told apart by their Spanish
        return Math.Max(score, 0);
    }

    private static double TitleSimilarity(string a, string b)
    {
        var na = Normalize(StripBrackets(a));
        var nb = Normalize(StripBrackets(b));
        if (na.Length == 0 || nb.Length == 0)
        {
            return 0;
        }

        if (na == nb)
        {
            return 1;
        }

        if (na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal))
        {
            return 0.85;
        }

        var ta = Tokens(na);
        var tb = Tokens(nb);
        var common = ta.Intersect(tb).Count();
        return (double)common / Math.Max(ta.Count, tb.Count);
    }

    private static HashSet<string> Tokens(string text)
        => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 1).ToHashSet(StringComparer.Ordinal);

    /// <summary>Lower case, without accents or punctuation.</summary>
    internal static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return Spaces().Replace(sb.ToString(), " ").Trim();
    }

    private static string StripBrackets(string text) => Brackets().Replace(text, " ");

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static List<string> Strings(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList()
            : [];

    private static List<string> Names(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => Str(x, "name")).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    private static List<string> SplitNames(string? names)
        => string.IsNullOrWhiteSpace(names) ? [] : names.Split([" & ", ", ", " y "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static int? ParseIndex(string? sequence)
    {
        if (string.IsNullOrEmpty(sequence))
        {
            return null;
        }

        var digits = LeadingNumber().Match(sequence);
        return digits.Success && int.TryParse(digits.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static DateTime? Date(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length == 4 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
        {
            return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }

    /// <summary>HTML descriptions to plain text with paragraphs.</summary>
    private static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = LineBreaks().Replace(html, "\n");
        text = Tags().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = BlankLines().Replace(text, "\n\n").Trim();
        return text.Length > 0 ? text : null;
    }

    // "Anna Kadabra 1 El club…", "Saga libro 2 - Title", "Saga #3: Title" (dots are already spaces)
    [GeneratedRegex(@"^\D{3,}?\s(?:libro\s|tomo\s|vol\s|volumen\s|book\s|#)?\d{1,3}\s*[:\-–,]?\s+(?<title>\D.{2,})$", RegexOptions.IgnoreCase)]
    private static partial Regex SagaPrefix();

    [GeneratedRegex(@"\d+x\d+bb")]
    private static partial Regex ArtworkSize();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"^\d+")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLines();
}
