using System;

namespace MediaBrowser.Model.Discovery;

/// <summary>
/// A title for the home banner: a recent release ranked by popularity.
/// </summary>
public class HeroPick
{
    /// <summary>
    /// Gets or sets the movie or series id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets why it was picked: "Movie" (recent release), "NewSeries" or "NewEpisodes".
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of episodes released in the window (series).
    /// </summary>
    public int NewEpisodes { get; set; }

    /// <summary>
    /// Gets or sets the release date of the movie, or of the last new episode.
    /// </summary>
    public DateTime? ReleaseDate { get; set; }

    /// <summary>
    /// Gets or sets the popularity score used to rank.
    /// </summary>
    public double Score { get; set; }

    /// <summary>
    /// Gets or sets the IMDb rating.
    /// </summary>
    public float? ImdbRating { get; set; }

    /// <summary>
    /// Gets or sets the IMDb votes.
    /// </summary>
    public int? ImdbVotes { get; set; }

    /// <summary>
    /// Gets or sets the TMDb popularity.
    /// </summary>
    public double? TmdbPopularity { get; set; }
}
