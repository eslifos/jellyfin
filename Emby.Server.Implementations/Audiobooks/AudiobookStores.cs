using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Library;

namespace Emby.Server.Implementations.Audiobooks;

/// <summary>
/// Which audiobook stores to ask for a language and country: the store of the language, the store of the country and
/// the US store, which has the widest catalog.
/// </summary>
internal static class AudiobookStores
{
    private const string Spanish = "es";
    private const string UnitedStates = "us";

    private static readonly Dictionary<string, string> _audibleHosts = new(StringComparer.Ordinal)
    {
        [UnitedStates] = "https://api.audible.com",
        [Spanish] = "https://api.audible.es",
        ["fr"] = "https://api.audible.fr",
        ["de"] = "https://api.audible.de",
        ["it"] = "https://api.audible.it",
        ["gb"] = "https://api.audible.co.uk",
        ["ca"] = "https://api.audible.ca",
        ["au"] = "https://api.audible.com.au",
        ["jp"] = "https://api.audible.co.jp",
        ["in"] = "https://api.audible.in",
        ["br"] = "https://api.audible.com.br"
    };

    private static readonly Dictionary<string, string> _storeOfLanguage = new(StringComparer.Ordinal)
    {
        [Spanish] = Spanish,
        ["fr"] = "fr",
        ["de"] = "de",
        ["it"] = "it",
        ["ja"] = "jp",
        ["pt"] = "br"
    };

    private static readonly Dictionary<string, string> _languageNames = new(StringComparer.Ordinal)
    {
        ["en"] = "english",
        [Spanish] = "spanish",
        ["fr"] = "french",
        ["de"] = "german",
        ["it"] = "italian",
        ["pt"] = "portuguese",
        ["ja"] = "japanese",
        ["nl"] = "dutch",
        ["ru"] = "russian",
        ["zh"] = "chinese",
        ["ko"] = "korean",
        ["pl"] = "polish",
        ["sv"] = "swedish"
    };

    /// <summary>Gets the Audible stores to ask. The Spanish catalog of the US store is Latin American.</summary>
    /// <param name="locale">The language and country of the library.</param>
    /// <returns>The stores, best first.</returns>
    public static List<(string Region, string Host)> Audible(MetadataLocale locale)
        => Stores(locale).Where(_audibleHosts.ContainsKey).Select(s => (s, _audibleHosts[s])).ToList();

    /// <summary>Gets the Apple Books stores to ask; a country without a store simply answers nothing.</summary>
    /// <param name="locale">The language and country of the library.</param>
    /// <returns>The country codes, best first.</returns>
    public static List<string> Apple(MetadataLocale locale) => Stores(locale);

    /// <summary>Gets the language a store mainly sells in, when it has one of its own.</summary>
    /// <param name="store">The store.</param>
    /// <returns>The language code, or null.</returns>
    public static string? LanguageOf(string store)
    {
        foreach (var (language, own) in _storeOfLanguage)
        {
            if (own == store)
            {
                return language;
            }
        }

        return null;
    }

    /// <summary>Tells whether the language a catalog reports ("french", "fr", "fr-CA") is the wanted one.</summary>
    /// <param name="reported">What the catalog says.</param>
    /// <param name="language">The wanted language code.</param>
    /// <returns>Whether they are the same language.</returns>
    public static bool IsLanguage(string? reported, string language)
    {
        var text = reported?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(text)
            && (text == language
                || text.StartsWith(language + "-", StringComparison.Ordinal)
                || (_languageNames.TryGetValue(language, out var name) && text == name));
    }

    private static List<string> Stores(MetadataLocale locale)
    {
        // Latin American and Spain editions are told apart afterwards, so Spanish always asks both stores
        if (locale.Language == Spanish)
        {
            return [UnitedStates, Spanish];
        }

        var stores = new List<string>();
        if (_storeOfLanguage.TryGetValue(locale.Language, out var ofLanguage))
        {
            stores.Add(ofLanguage);
        }

        stores.Add(locale.Country);
        stores.Add(UnitedStates);
        return stores.Distinct(StringComparer.Ordinal).ToList();
    }
}
