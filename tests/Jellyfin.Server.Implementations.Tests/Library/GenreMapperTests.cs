using System.Collections.Generic;
using Emby.Server.Implementations.Library.Genres;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class GenreMapperTests
{
    private static readonly IReadOnlyDictionary<string, string> _spanish = GenreMapper.BuildLookup(GenreDefaults.ByLanguage["es"]);

    [Theory]
    [InlineData("Comedy", "Comedia")]
    [InlineData("Animation", "Animación")]
    [InlineData("Action", "Acción")]
    [InlineData("Science Fiction", "Ciencia ficción")]
    [InlineData("Thriller", "Suspense")]
    [InlineData("Sci-Fi & Fantasy", "Ciencia ficción y fantasía")]
    [InlineData("Slice of Life", "Recuentos de la vida")] // AniList
    public void Map_EnglishGenre_SpanishName(string genre, string expected)
    {
        Assert.Equal([expected], GenreMapper.Map([genre], _spanish));
    }

    [Theory]
    [InlineData("comedy")]
    [InlineData("  COMEDY ")]
    public void Map_AnyCaseOrSpaces_SameGenre(string genre)
    {
        Assert.Equal(["Comedia"], GenreMapper.Map([genre], _spanish));
    }

    [Theory]
    [InlineData("Science-Fiction")]
    [InlineData("science_fiction")]
    [InlineData("Sci Fi and Fantasy", "Ciencia ficción y fantasía")]
    [InlineData("Sci-Fi/Fantasy", "Ciencia ficción y fantasía")]
    [InlineData("Film-Noir", "Cine negro")]
    public void Map_OtherSpellings_SameGenre(string genre, string expected = "Ciencia ficción")
    {
        Assert.Equal([expected], GenreMapper.Map([genre], _spanish));
    }

    [Fact]
    public void Map_BothLanguages_OnlyOnce()
    {
        Assert.Equal(
            ["Comedia", "Animación", "Acción"],
            GenreMapper.Map(["Comedy", "Comedia", "Animation", "Animación", "Action", "Acción"], _spanish));
    }

    [Fact]
    public void Map_KeepsTheOrderOfFirstAppearance()
    {
        Assert.Equal(["Drama", "Comedia", "Terror"], GenreMapper.Map(["Drama", "Comedia", "Comedy", "Horror"], _spanish));
    }

    [Fact]
    public void Map_SpellingOfTheLanguage_WrittenTheSameWay()
    {
        // "Ciencia Ficción" (another catalog) and "Ciencia ficción" (the map) are the same genre
        Assert.Equal(["Ciencia ficción"], GenreMapper.Map(["Ciencia Ficción", "Sci-Fi"], _spanish));
    }

    [Fact]
    public void Map_UnknownGenre_KeptAsItIs()
    {
        Assert.Equal(["Isekai", "Comedia"], GenreMapper.Map(["Isekai", " Comedy"], _spanish));
    }

    [Fact]
    public void Map_EmptyNames_Dropped()
    {
        Assert.Equal(["Comedia"], GenreMapper.Map([string.Empty, "  ", "Comedy"], _spanish));
    }

    [Fact]
    public void BuildLookup_TheGenreMappedFrom_WinsOverASpelling()
    {
        // An administrator maps "Romance" to "Romántica": the built-in "Romance" spelling must not undo it
        var lookup = GenreMapper.BuildLookup(new Dictionary<string, string>
        {
            ["Romance"] = "Romántica",
            ["Love"] = "Romance"
        });
        Assert.Equal(["Romántica"], GenreMapper.Map(["Romance"], lookup));
    }

    [Theory]
    [InlineData("Action & Adventure", "action and adventure")]
    [InlineData("  Sci-Fi  /  Fantasy ", "sci fi and fantasy")]
    [InlineData("Mini_Series", "mini series")]
    public void Key_NormalizesCaseSeparatorsAndSpaces(string genre, string expected)
    {
        Assert.Equal(expected, GenreMapper.Key(genre));
    }
}
