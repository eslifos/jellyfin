using System;
using System.IO;
using System.Linq;
using Emby.Server.Implementations.Library.Genres;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Genres;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class GenreNormalizerTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "genre-map-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, true);
        }
    }

    private GenreNormalizer Create()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_dataPath);
        return new GenreNormalizer(paths.Object, NullLogger<GenreNormalizer>.Instance);
    }

    private static Movie MovieIn(string language, params string[] genres)
        => new() { PreferredMetadataLanguage = language, Genres = genres };

    [Fact]
    public void Normalize_SpanishLibrary_EnglishGenresMapped()
    {
        var movie = MovieIn("es-MX", "Comedy", "Comedia", "Animation");
        Assert.True(Create().Normalize(movie));
        Assert.Equal(["Comedia", "Animación"], movie.Genres);
    }

    [Fact]
    public void Normalize_AlreadyInTheLanguage_Unchanged()
    {
        var movie = MovieIn("es", "Comedia", "Drama");
        Assert.False(Create().Normalize(movie));
        Assert.Equal(["Comedia", "Drama"], movie.Genres);
    }

    [Fact]
    public void Normalize_EnglishLibrary_Unchanged()
    {
        var movie = MovieIn("en", "Comedy", "Animation");
        Assert.False(Create().Normalize(movie));
        Assert.Equal(["Comedy", "Animation"], movie.Genres);
    }

    [Fact]
    public void Normalize_LockedGenres_Unchanged()
    {
        var movie = MovieIn("es", "Comedy");
        movie.LockedFields = [MetadataField.Genres];
        Assert.False(Create().Normalize(movie));
        Assert.Equal(["Comedy"], movie.Genres);
    }

    [Fact]
    public void Normalize_Music_Unchanged()
    {
        var album = new MusicAlbum { PreferredMetadataLanguage = "es", Genres = ["Music", "Comedy"] };
        Assert.False(Create().Normalize(album));
        Assert.Equal(["Music", "Comedy"], album.Genres);
    }

    [Fact]
    public void SetMap_SavedAndUsed_AlsoAfterARestart()
    {
        Create().SetMap("es", [new GenreMapPair("Comedy", "Humor"), new GenreMapPair("Isekai", "Otro mundo")]);

        var movie = MovieIn("es", "Comedy", "Isekai", "Animation");
        Assert.True(Create().Normalize(movie));
        // The saved list replaces the built-in one of that language: "Animation" is no longer in it
        Assert.Equal(["Humor", "Otro mundo", "Animation"], movie.Genres);
    }

    [Fact]
    public void ResetMap_BackToTheBuiltInEquivalences()
    {
        var normalizer = Create();
        normalizer.SetMap("es", [new GenreMapPair("Comedy", "Humor")]);
        normalizer.ResetMap("es");

        var movie = MovieIn("es", "Comedy");
        normalizer.Normalize(movie);
        Assert.Equal(["Comedia"], movie.Genres);
    }

    [Fact]
    public void GetMap_MarksBuiltInCustomAndInUse()
    {
        var normalizer = Create();
        normalizer.SetMap("es", [new GenreMapPair("Comedy", "Comedia"), new GenreMapPair("Isekai", "Otro mundo")]);

        var spanish = normalizer.GetMap(["es-MX"]).Languages.Single(l => l.Language == "es");
        Assert.True(spanish.IsCustom);
        Assert.True(spanish.InUse);
        Assert.True(spanish.Entries.Single(e => e.From == "Comedy").IsDefault);
        Assert.False(spanish.Entries.Single(e => e.From == "Isekai").IsDefault);

        var map = normalizer.GetMap(["es"]);
        Assert.Equal("es", map.Languages[0].Language); // the languages in use come first
        Assert.False(map.Languages.Single(l => l.Language == "fr").IsCustom);
    }

    [Theory]
    [InlineData("spanish")]
    [InlineData("e")]
    [InlineData("../x")]
    public void SetMap_NotALanguageCode_Refused(string language)
    {
        Assert.Throws<ArgumentException>(() => Create().SetMap(language, []));
    }

    [Fact]
    public void SetMap_EmptyName_Refused()
    {
        Assert.Throws<ArgumentException>(() => Create().SetMap("es", [new GenreMapPair("Comedy", " ")]));
    }

    [Fact]
    public void GetSourceGenres_IncludesBuiltInAndSaved()
    {
        var normalizer = Create();
        normalizer.SetMap("pt", [new GenreMapPair("Isekai", "Outro mundo")]);
        var sources = normalizer.GetSourceGenres();
        Assert.Contains("Comedy", sources);
        Assert.Contains("Isekai", sources);
    }
}
