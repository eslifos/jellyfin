namespace MediaBrowser.Model.PlaybackStats;

/// <summary>
/// Totals of a period.
/// </summary>
public class PlaybackStatsTotals
{
    /// <summary>
    /// Gets or sets the number of plays.
    /// </summary>
    public int Plays { get; set; }

    /// <summary>
    /// Gets or sets the seconds watched.
    /// </summary>
    public long WatchedSeconds { get; set; }

    /// <summary>
    /// Gets or sets the number of distinct users that played something.
    /// </summary>
    public int ActiveUsers { get; set; }

    /// <summary>
    /// Gets or sets the average watched seconds per play.
    /// </summary>
    public long AverageSessionSeconds { get; set; }

    /// <summary>
    /// Gets or sets the percentage of plays watched to the end.
    /// </summary>
    public double CompletedPercent { get; set; }

    /// <summary>
    /// Gets or sets the percentage of plays that had to be transcoded.
    /// </summary>
    public double TranscodePercent { get; set; }
}
