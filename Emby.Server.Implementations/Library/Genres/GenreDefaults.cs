using System;
using System.Collections.Generic;
using System.Collections.Frozen;

namespace Emby.Server.Implementations.Library.Genres;

/// <summary>
/// The built-in equivalences, English → language of the library: the genres of TMDb (movies and series), AniList and
/// the usual ones of NFO files. Each administrator can change them from the dashboard.
/// </summary>
public static class GenreDefaults
{
    /// <summary>
    /// Gets the built-in equivalences, by language code.
    /// </summary>
    public static FrozenDictionary<string, FrozenDictionary<string, string>> ByLanguage { get; } = new Dictionary<string, FrozenDictionary<string, string>>
    {
        ["es"] = Freeze(new()
        {
            // TMDb, movies
            ["Action"] = "Acción",
            ["Adventure"] = "Aventura",
            ["Animation"] = "Animación",
            ["Comedy"] = "Comedia",
            ["Crime"] = "Crimen",
            ["Documentary"] = "Documental",
            ["Family"] = "Familia",
            ["Fantasy"] = "Fantasía",
            ["History"] = "Historia",
            ["Horror"] = "Terror",
            ["Music"] = "Música",
            ["Mystery"] = "Misterio",
            ["Science Fiction"] = "Ciencia ficción",
            ["TV Movie"] = "Película de TV",
            ["Thriller"] = "Suspense",
            ["War"] = "Bélica",
            // TMDb, series
            ["Action & Adventure"] = "Acción y aventura",
            ["Kids"] = "Infantil",
            ["News"] = "Noticias",
            ["Sci-Fi & Fantasy"] = "Ciencia ficción y fantasía",
            ["Soap"] = "Telenovela",
            ["Talk"] = "Entrevistas",
            ["War & Politics"] = "Bélica y política",
            // AniList
            ["Mahou Shoujo"] = "Chicas mágicas",
            ["Psychological"] = "Psicológico",
            ["Sci-Fi"] = "Ciencia ficción",
            ["Slice of Life"] = "Recuentos de la vida",
            ["Sports"] = "Deportes",
            ["Supernatural"] = "Sobrenatural",
            // NFO files and other catalogs
            ["Biography"] = "Biografía",
            ["Children"] = "Infantil",
            ["Christmas"] = "Navidad",
            ["Disaster"] = "Catástrofes",
            ["Film Noir"] = "Cine negro",
            ["Food"] = "Gastronomía",
            ["Game Show"] = "Concurso",
            ["Home and Garden"] = "Hogar y jardín",
            ["Indie"] = "Independiente",
            ["Martial Arts"] = "Artes marciales",
            ["Mini Series"] = "Miniserie",
            ["Romantic Comedy"] = "Comedia romántica",
            ["Short"] = "Cortometraje",
            ["Sitcom"] = "Comedia de situación",
            ["Sport"] = "Deportes",
            ["Superhero"] = "Superhéroes",
            ["Talk Show"] = "Entrevistas",
            ["Travel"] = "Viajes"
        }),
        ["pt"] = Freeze(new()
        {
            ["Action"] = "Ação",
            ["Adventure"] = "Aventura",
            ["Animation"] = "Animação",
            ["Comedy"] = "Comédia",
            ["Documentary"] = "Documentário",
            ["Family"] = "Família",
            ["Fantasy"] = "Fantasia",
            ["History"] = "História",
            ["Horror"] = "Terror",
            ["Music"] = "Música",
            ["Mystery"] = "Mistério",
            ["Science Fiction"] = "Ficção científica",
            ["Sci-Fi"] = "Ficção científica",
            ["TV Movie"] = "Cinema TV",
            ["War"] = "Guerra",
            ["Western"] = "Faroeste"
        }),
        ["fr"] = Freeze(new()
        {
            ["Adventure"] = "Aventure",
            ["Comedy"] = "Comédie",
            ["Documentary"] = "Documentaire",
            ["Drama"] = "Drame",
            ["Family"] = "Familial",
            ["Fantasy"] = "Fantastique",
            ["History"] = "Histoire",
            ["Horror"] = "Horreur",
            ["Music"] = "Musique",
            ["Mystery"] = "Mystère",
            ["Science Fiction"] = "Science-Fiction",
            ["Sci-Fi"] = "Science-Fiction",
            ["TV Movie"] = "Téléfilm",
            ["War"] = "Guerre"
        }),
        ["de"] = Freeze(new()
        {
            ["Adventure"] = "Abenteuer",
            ["Comedy"] = "Komödie",
            ["Crime"] = "Krimi",
            ["Documentary"] = "Dokumentarfilm",
            ["Family"] = "Familie",
            ["History"] = "Historie",
            ["Music"] = "Musik",
            ["Romance"] = "Liebesfilm",
            ["TV Movie"] = "TV-Film",
            ["War"] = "Kriegsfilm"
        }),
        ["it"] = Freeze(new()
        {
            ["Action"] = "Azione",
            ["Adventure"] = "Avventura",
            ["Animation"] = "Animazione",
            ["Comedy"] = "Commedia",
            ["Documentary"] = "Documentario",
            ["Drama"] = "Dramma",
            ["Family"] = "Famiglia",
            ["History"] = "Storia",
            ["Music"] = "Musica",
            ["Mystery"] = "Mistero",
            ["Science Fiction"] = "Fantascienza",
            ["Sci-Fi"] = "Fantascienza",
            ["TV Movie"] = "Film TV",
            ["War"] = "Guerra"
        })
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static FrozenDictionary<string, string> Freeze(Dictionary<string, string> entries)
        => entries.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}
