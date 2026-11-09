using System;

namespace MediaBrowser.Model.Discovery;

/// <summary>
/// An upcoming release found on TMDb that is not in the library yet: the next episode of a series of the
/// library, or a future movie of a saga (TMDb collection) the library has movies of.
/// </summary>
public class UpcomingRelease
{
    /// <summary>
    /// Gets or sets the kind: "Episode" or "Movie".
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TMDb id of the movie or series.
    /// </summary>
    public int TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the title (movie title or episode name).
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release / air date.
    /// </summary>
    public DateTime ReleaseDate { get; set; }

    /// <summary>
    /// Gets or sets the overview.
    /// </summary>
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets the poster (or still) URL on TMDb.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Gets or sets the series of the library (episodes).
    /// </summary>
    public Guid? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the series name (episodes).
    /// </summary>
    public string? SeriesName { get; set; }

    /// <summary>
    /// Gets or sets the season number (episodes).
    /// </summary>
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number (episodes).
    /// </summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets the saga name (movies).
    /// </summary>
    public string? CollectionName { get; set; }

    /// <summary>
    /// Gets or sets a movie of the library from the same saga (movies), to link to.
    /// </summary>
    public Guid? RelatedItemId { get; set; }
}
