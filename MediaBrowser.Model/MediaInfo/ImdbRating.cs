namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// IMDb rating of a title (from the official IMDb datasets).
/// </summary>
public class ImdbRating
{
    /// <summary>
    /// Gets or sets the IMDb id (tt…).
    /// </summary>
    public string ImdbId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the average rating (0-10).
    /// </summary>
    public float Rating { get; set; }

    /// <summary>
    /// Gets or sets the number of votes.
    /// </summary>
    public int Votes { get; set; }
}
