using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.MusicHub;

/// <summary>
/// Finds biographies, album descriptions and tags on Wikipedia and Last.fm. Wikidata says which article belongs to
/// which artist or album, so two artists with the same name are not mixed up; when it is not clear, nothing is used.
/// </summary>
internal sealed partial class MusicTextFinder
{
    private const string WikidataUrl = "https://www.wikidata.org/w/api.php?format=json&";
    private const string LastFmUrl = "https://ws.audioscrobbler.com/2.0/?format=json&autocorrect=0&";
    private const string MusicBrainzArtistProperty = "P434";
    private const string MusicBrainzReleaseGroupProperty = "P436";
    private const string PerformerProperty = "P175";
    private const string InstanceProperty = "P31";
    private const string PublishedProperty = "P577";
    private const int LastFmRateLimitError = 29;
    private const int MaxTextLength = 2500;
    private const int MinTextLength = 40;
    private const int MaxTags = 3;
    private const int MaxPerformers = 3;
    private const int MaxTitlesToConfirm = 2;

    // Album, studio album, live album, compilation, EP and soundtrack
    private static readonly HashSet<string> _albumKinds = ["Q482994", "Q208569", "Q209939", "Q222910", "Q169930", "Q4176708"];

    private static readonly HashSet<string> _uselessTags = new(StringComparer.Ordinal)
    {
        "seenlive", "albumsiown", "favorite", "favorites", "favourite", "favourites", "favoritealbums", "favouritealbums",
        "love", "awesome", "beautiful", "amazing", "myalbums", "vinyl", "cd", "spotify", "albumoftheyear"
    };

    private readonly MusicCatalog _wikipedia;
    private readonly MusicCatalog _lastFm;
    private readonly ILogger _logger;
    private readonly Dictionary<string, List<WikiItem>> _candidates = new(StringComparer.Ordinal);

    public MusicTextFinder(MusicCatalog wikipedia, MusicCatalog lastFm, ILogger logger)
    {
        _wikipedia = wikipedia;
        _lastFm = lastFm;
        _logger = logger;
    }

    /// <summary>Starts a new run.</summary>
    public void Reset()
    {
        _candidates.Clear();
        _wikipedia.Failures = 0;
        _lastFm.Failures = 0;
    }

    public Task<MusicTextResult> FindArtistAsync(string name, string? musicBrainzId, IReadOnlyList<string> albumTitles, MetadataLocale locale, bool wantsText, bool wantsTags, string? lastFmKey, CancellationToken cancellationToken)
        => ComposeAsync(
            () => ResolveArtistAsync(name, musicBrainzId, albumTitles, locale.Language, cancellationToken),
            key => ArtistFromLastFmAsync(name, key, locale.Language, cancellationToken),
            locale,
            wantsText,
            wantsTags,
            lastFmKey,
            cancellationToken);

    public Task<MusicTextResult> FindAlbumAsync(string artist, string title, int? year, string? releaseGroupId, MetadataLocale locale, bool wantsText, bool wantsTags, string? lastFmKey, CancellationToken cancellationToken)
        => ComposeAsync(
            () => ResolveAlbumAsync(artist, title, year, releaseGroupId, locale.Language, cancellationToken),
            key => AlbumFromLastFmAsync(artist, title, key, locale.Language, cancellationToken),
            locale,
            wantsText,
            wantsTags,
            lastFmKey,
            cancellationToken);

    /// <summary>Asks Last.fm something simple to know whether a key works.</summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the key is valid.</returns>
    public async Task<bool> IsValidKeyAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var url = LastFmUrl + "method=artist.getinfo&artist=Radiohead&api_key=" + Uri.EscapeDataString(key);
            using var answer = await _lastFm.GetJsonAsync<JsonDocument>(url, cancellationToken).ConfigureAwait(false);
            return answer is not null && Child(answer.RootElement, "artist").HasValue;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            _lastFm.Ready();
            return false;
        }
    }

    private static string Escape(string text) => Uri.EscapeDataString(text);

    private static JsonElement? Child(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : null;

    private static string? Text(JsonElement element, string name)
        => Child(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static string? Shorten(string? text)
    {
        var clean = text?.Trim();
        if (string.IsNullOrEmpty(clean) || clean.Length < MinTextLength)
        {
            return null;
        }

        if (clean.Length <= MaxTextLength)
        {
            return clean;
        }

        var cut = clean.LastIndexOf(". ", MaxTextLength, StringComparison.Ordinal);
        return cut > MinTextLength ? clean[..(cut + 1)] : clean[..MaxTextLength].TrimEnd() + "…";
    }

    private static string? CleanLastFmText(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var text = LinkRegex().Replace(content, string.Empty);
        text = TagRegex().Replace(text, string.Empty);
        text = LicenseRegex().Replace(WebUtility.HtmlDecode(text), string.Empty);
        // Last.fm keeps several artists with the same name on one page: that text does not describe this one
        return SharedPageRegex().IsMatch(text.TrimStart()) ? null : Shorten(text);
    }

    private static string[] CleanTags(JsonElement parent, string ownName)
    {
        if (Child(parent, "tags") is not { } tags || Child(tags, "tag") is not { } tag)
        {
            return [];
        }

        var names = tag.ValueKind switch
        {
            JsonValueKind.Array => tag.EnumerateArray().Select(t => Text(t, "name")),
            JsonValueKind.Object => [Text(tag, "name")],
            _ => []
        };
        var own = MusicNames.Comparable(ownName);
        return names
            .Where(n => !string.IsNullOrWhiteSpace(n) && !n!.Any(char.IsDigit))
            .Select(n => n!.Trim())
            .Where(n => MusicNames.Comparable(n) != own && !_uselessTags.Contains(MusicNames.Comparable(n)))
            .Select(n => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTags)
            .ToArray();
    }

    private void ThrowOnLastFmLimit(JsonElement root)
    {
        if (Child(root, "error") is { ValueKind: JsonValueKind.Number } error && error.GetInt32() == LastFmRateLimitError)
        {
            _lastFm.BackOff();
            throw new HttpRequestException("Last.fm asked to wait", null, HttpStatusCode.TooManyRequests);
        }
    }

    private async Task<MusicTextResult> ComposeAsync(
        Func<Task<WikiItem?>> findWiki,
        Func<string, Task<LastFmInfo?>> findLastFm,
        MetadataLocale locale,
        bool wantsText,
        bool wantsTags,
        string? lastFmKey,
        CancellationToken cancellationToken)
    {
        var complete = true;
        string? text = null;
        string[] tags = [];
        WikiItem? item = null;

        if (wantsText)
        {
            (item, var ok) = await TryAsync(_wikipedia, findWiki).ConfigureAwait(false);
            complete &= ok;
            if (item?.LocalTitle is { } local)
            {
                (text, ok) = await TryAsync(_wikipedia, () => ArticleAsync(locale.Language, local, cancellationToken)).ConfigureAwait(false);
                complete &= ok;
            }
        }

        if (!string.IsNullOrEmpty(lastFmKey) && (wantsTags || (wantsText && text is null)))
        {
            var (info, ok) = await TryAsync(_lastFm, () => findLastFm(lastFmKey)).ConfigureAwait(false);
            complete &= ok;
            if (info is not null)
            {
                tags = wantsTags ? info.Tags : [];
                if (wantsText && text is null && info.Text is not null)
                {
                    text = info.Text + "\n\n— Last.fm";
                }
            }
        }

        if (wantsText && text is null && !locale.IsFallbackLanguage && item?.EnglishTitle is { } english)
        {
            (text, var ok) = await TryAsync(_wikipedia, () => ArticleAsync(MetadataLocale.FallbackLanguage, english, cancellationToken)).ConfigureAwait(false);
            complete &= ok;
        }

        return new MusicTextResult(text, tags, complete);
    }

    private async Task<(T? Value, bool Ok)> TryAsync<T>(MusicCatalog catalog, Func<Task<T?>> step)
        where T : class
    {
        if (catalog.IsOff)
        {
            return (null, false);
        }

        try
        {
            var value = await step().ConfigureAwait(false);
            catalog.Failures = 0;
            return (value, true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "{Catalog} did not answer", catalog.Name);
            catalog.Failures++;
            return (null, false);
        }
    }

    private async Task<WikiItem?> ResolveArtistAsync(string name, string? musicBrainzId, IReadOnlyList<string> albumTitles, string language, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(musicBrainzId, out _))
        {
            var ids = await SearchAsync("haswbstatement:" + MusicBrainzArtistProperty + "=" + musicBrainzId, cancellationToken).ConfigureAwait(false);
            var exact = (await GetItemsAsync(ids.Take(1), language, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            if (exact is not null)
            {
                return exact;
            }
        }

        // Many artists share a name: only the one who really has one of these albums counts
        var confirmed = new List<WikiItem>();
        foreach (var candidate in await CandidatesAsync(name, language, cancellationToken).ConfigureAwait(false))
        {
            foreach (var title in albumTitles.Take(MaxTitlesToConfirm))
            {
                if (await MatchAlbumAsync(candidate, title, null, language, cancellationToken).ConfigureAwait(false) is not null)
                {
                    confirmed.Add(candidate);
                    break;
                }
            }
        }

        return confirmed.Count == 1 ? confirmed[0] : null;
    }

    private async Task<List<WikiItem>> CandidatesAsync(string name, string language, CancellationToken cancellationToken)
    {
        var key = MusicNames.Comparable(name);
        var memoryKey = language + ":" + key;
        if (_candidates.TryGetValue(memoryKey, out var known))
        {
            return known;
        }

        // Wikidata rejects the few languages it does not know, and those search in English
        var ids = await SearchNameAsync(name, language, cancellationToken).ConfigureAwait(false);
        if (ids is null && language != MetadataLocale.FallbackLanguage)
        {
            ids = await SearchNameAsync(name, MetadataLocale.FallbackLanguage, cancellationToken).ConfigureAwait(false);
        }

        var items = await GetItemsAsync(ids ?? [], language, cancellationToken).ConfigureAwait(false);
        var same = items
            .Where(i => i.Has(MusicBrainzArtistProperty) && i.Names.Any(n => MusicNames.Comparable(n) == key))
            .Take(MaxPerformers)
            .ToList();
        _candidates[memoryKey] = same;
        return same;
    }

    private async Task<List<string>?> SearchNameAsync(string name, string language, CancellationToken cancellationToken)
    {
        var url = WikidataUrl + "action=wbsearchentities&type=item&limit=10&language=" + language + "&uselang=" + language + "&search=" + Escape(name);
        using var answer = await _wikipedia.GetJsonAsync<JsonDocument>(url, cancellationToken).ConfigureAwait(false);
        return answer is not null && Child(answer.RootElement, "search") is { ValueKind: JsonValueKind.Array } found
            ? found.EnumerateArray().Select(r => Text(r, "id")).OfType<string>().ToList()
            : null;
    }

    private async Task<WikiItem?> ResolveAlbumAsync(string artist, string title, int? year, string? releaseGroupId, string language, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(releaseGroupId, out _))
        {
            var ids = await SearchAsync("haswbstatement:" + MusicBrainzReleaseGroupProperty + "=" + releaseGroupId, cancellationToken).ConfigureAwait(false);
            var exact = (await GetItemsAsync(ids.Take(1), language, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            if (exact is not null)
            {
                return exact;
            }
        }

        var matches = new List<WikiItem>();
        foreach (var performer in await CandidatesAsync(artist, language, cancellationToken).ConfigureAwait(false))
        {
            var album = await MatchAlbumAsync(performer, title, year, language, cancellationToken).ConfigureAwait(false);
            if (album is not null)
            {
                matches.Add(album);
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task<WikiItem?> MatchAlbumAsync(WikiItem performer, string title, int? year, string language, CancellationToken cancellationToken)
    {
        var words = SearchWordsRegex().Replace(MusicNames.CleanTitle(title), " ").Trim();
        if (words.Length == 0)
        {
            return null;
        }

        var ids = await SearchAsync("\"" + words + "\" haswbstatement:" + PerformerProperty + "=" + performer.Id, cancellationToken).ConfigureAwait(false);
        var items = await GetItemsAsync(ids, language, cancellationToken).ConfigureAwait(false);
        return items
            .Where(i => i.Names.Count > 0 && i.Values(InstanceProperty).Any(_albumKinds.Contains))
            .Where(i => MusicNames.FitsYear(year, i.Year))
            .Select(i => (Item: i, Score: i.Names.Max(n => MusicNames.TitleScore(title, n))))
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .Select(c => c.Item)
            .FirstOrDefault();
    }

    private async Task<List<string>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var url = WikidataUrl + "action=query&list=search&srlimit=5&srsearch=" + Escape(query);
        using var answer = await _wikipedia.GetJsonAsync<JsonDocument>(url, cancellationToken).ConfigureAwait(false);
        if (answer is null || Child(answer.RootElement, "query") is not { } result || Child(result, "search") is not { ValueKind: JsonValueKind.Array } rows)
        {
            return [];
        }

        return rows.EnumerateArray().Select(r => Text(r, "title")).OfType<string>().ToList();
    }

    private async Task<List<WikiItem>> GetItemsAsync(IEnumerable<string> ids, string language, CancellationToken cancellationToken)
    {
        var list = string.Join('|', ids);
        if (list.Length == 0)
        {
            return [];
        }

        var url = WikidataUrl + "action=wbgetentities&props=labels|aliases|claims|sitelinks&languages=" + language + "|" + MetadataLocale.FallbackLanguage + "&sitefilter=" + language + "wiki|" + MetadataLocale.FallbackLanguage + "wiki&ids=" + list;
        using var answer = await _wikipedia.GetJsonAsync<JsonDocument>(url, cancellationToken).ConfigureAwait(false);
        if (answer is null || Child(answer.RootElement, "entities") is not { ValueKind: JsonValueKind.Object } entities)
        {
            return [];
        }

        return entities.EnumerateObject().Select(e => ReadItem(e.Name, e.Value, language)).ToList();
    }

    private static WikiItem ReadItem(string id, JsonElement entity, string language)
    {
        var names = new List<string>();
        if (Child(entity, "labels") is { ValueKind: JsonValueKind.Object } labels)
        {
            names.AddRange(labels.EnumerateObject().Select(l => Text(l.Value, "value")).OfType<string>());
        }

        if (Child(entity, "aliases") is { ValueKind: JsonValueKind.Object } aliases)
        {
            names.AddRange(aliases.EnumerateObject()
                .Where(a => a.Value.ValueKind == JsonValueKind.Array)
                .SelectMany(a => a.Value.EnumerateArray())
                .Select(a => Text(a, "value"))
                .OfType<string>());
        }

        var claims = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (Child(entity, "claims") is { ValueKind: JsonValueKind.Object } allClaims)
        {
            foreach (var property in new[] { InstanceProperty, MusicBrainzArtistProperty, PublishedProperty })
            {
                if (Child(allClaims, property) is { ValueKind: JsonValueKind.Array } values)
                {
                    claims[property] = values.EnumerateArray().Select(ClaimValue).OfType<string>().ToList();
                }
            }
        }

        var links = Child(entity, "sitelinks");
        var local = links is { } own && Child(own, language + "wiki") is { } ownLink ? Text(ownLink, "title") : null;
        var english = links is { } en && Child(en, MetadataLocale.FallbackLanguage + "wiki") is { } enLink ? Text(enLink, "title") : null;
        return new WikiItem(id, names, claims, local, english);
    }

    private static string? ClaimValue(JsonElement claim)
    {
        if (Child(claim, "mainsnak") is not { } snak || Child(snak, "datavalue") is not { } data || Child(data, "value") is not { } value)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => Text(value, "id") ?? Text(value, "time"),
            _ => null
        };
    }

    private async Task<string?> ArticleAsync(string language, string title, CancellationToken cancellationToken)
    {
        var url = "https://" + language + ".wikipedia.org/w/api.php?format=json&action=query&prop=extracts&exintro=1&explaintext=1&redirects=1&titles=" + Escape(title);
        using var answer = await _wikipedia.GetJsonAsync<JsonDocument>(url, cancellationToken).ConfigureAwait(false);
        if (answer is null || Child(answer.RootElement, "query") is not { } query || Child(query, "pages") is not { ValueKind: JsonValueKind.Object } pages)
        {
            return null;
        }

        var text = Shorten(pages.EnumerateObject().Select(p => Text(p.Value, "extract")).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)));
        return text is null ? null : text + "\n\n— Wikipedia";
    }

    private async Task<LastFmInfo?> ArtistFromLastFmAsync(string name, string key, string language, CancellationToken cancellationToken)
    {
        var url = LastFmUrl + "lang=" + language + "&method=artist.getinfo&artist=" + Escape(name) + "&api_key=" + Escape(key);
        using var answer = await _lastFm.GetJsonAsync<JsonDocument>(url, cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return null;
        }

        ThrowOnLastFmLimit(answer.RootElement);
        if (Child(answer.RootElement, "artist") is not { } artist || MusicNames.Comparable(Text(artist, "name")) != MusicNames.Comparable(name))
        {
            return null;
        }

        var text = Child(artist, "bio") is { } bio ? CleanLastFmText(Text(bio, "content")) : null;
        return new LastFmInfo(text, CleanTags(artist, name));
    }

    private async Task<LastFmInfo?> AlbumFromLastFmAsync(string artist, string title, string key, string language, CancellationToken cancellationToken)
    {
        var url = LastFmUrl + "lang=" + language + "&method=album.getinfo&artist=" + Escape(artist) + "&album=" + Escape(title) + "&api_key=" + Escape(key);
        using var answer = await _lastFm.GetJsonAsync<JsonDocument>(url, cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return null;
        }

        ThrowOnLastFmLimit(answer.RootElement);
        if (Child(answer.RootElement, "album") is not { } album
            || MusicNames.Comparable(Text(album, "artist")) != MusicNames.Comparable(artist)
            || MusicNames.TitleScore(title, Text(album, "name")) == 0)
        {
            return null;
        }

        var text = Child(album, "wiki") is { } wiki ? CleanLastFmText(Text(wiki, "content")) : null;
        return new LastFmInfo(text, CleanTags(album, artist));
    }

    [GeneratedRegex(@"<a\b[^>]*>.*?</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"User-contributed text is available.*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LicenseRegex();

    [GeneratedRegex(@"^(there (are|is|were)|hay|il y a|há|existem|es gibt|ci sono)\b.{0,50}\b(artists?|bands?|groups?|artistas?|bandas?|grupos?|artistes?|groupes?|künstler|gruppen|artisti|gruppi)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SharedPageRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex SearchWordsRegex();

    private sealed record LastFmInfo(string? Text, string[] Tags);

    private sealed record WikiItem(string Id, List<string> Names, Dictionary<string, List<string>> Claims, string? LocalTitle, string? EnglishTitle)
    {
        public int? Year
        {
            get
            {
                var time = Values(PublishedProperty).FirstOrDefault();
                return time?.Length > 5 && int.TryParse(time.AsSpan(1, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && MusicNames.IsValidYear(year)
                    ? year
                    : null;
            }
        }

        public bool Has(string property) => Claims.TryGetValue(property, out var values) && values.Count > 0;

        public IEnumerable<string> Values(string property) => Claims.TryGetValue(property, out var values) ? values : [];
    }
}

/// <summary>
/// What was found about an artist or an album.
/// </summary>
/// <param name="Text">The biography or description.</param>
/// <param name="Tags">The genres.</param>
/// <param name="Complete">Whether every catalog answered.</param>
internal sealed record MusicTextResult(string? Text, string[] Tags, bool Complete);
