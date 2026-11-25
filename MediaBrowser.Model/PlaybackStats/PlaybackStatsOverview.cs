using System.Collections.Generic;

namespace MediaBrowser.Model.PlaybackStats;

/// <summary>
/// Aggregated playback statistics for a period, compared with the period before.
/// </summary>
public class PlaybackStatsOverview
{
    /// <summary>
    /// Gets or sets the number of days covered.
    /// </summary>
    public int Days { get; set; }

    /// <summary>
    /// Gets or sets the totals of the period.
    /// </summary>
    public PlaybackStatsTotals Current { get; set; } = new();

    /// <summary>
    /// Gets or sets the totals of the period before (same length).
    /// </summary>
    public PlaybackStatsTotals Previous { get; set; } = new();

    /// <summary>
    /// Gets or sets the watch time per day.
    /// </summary>
    public IReadOnlyList<PlaybackStatsDay> Daily { get; set; } = [];

    /// <summary>
    /// Gets or sets the plays started per hour of the day (0-23, local time).
    /// </summary>
    public IReadOnlyList<int> PlaysByHour { get; set; } = [];

    /// <summary>
    /// Gets or sets the seconds watched per day of the week (Monday first).
    /// </summary>
    public IReadOnlyList<long> SecondsByWeekday { get; set; } = [];

    /// <summary>
    /// Gets or sets the top users.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> TopUsers { get; set; } = [];

    /// <summary>
    /// Gets or sets the most watched series.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> TopSeries { get; set; } = [];

    /// <summary>
    /// Gets or sets the most watched movies.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> TopMovies { get; set; } = [];

    /// <summary>
    /// Gets or sets the most used clients.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> TopClients { get; set; } = [];

    /// <summary>
    /// Gets or sets the plays per play method.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> PlayMethods { get; set; } = [];

    /// <summary>
    /// Gets or sets the time per kind of library.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> ByCategory { get; set; } = [];

    /// <summary>
    /// Gets or sets the most played artists.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> TopArtists { get; set; } = [];

    /// <summary>
    /// Gets or sets the most played albums.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> TopAlbums { get; set; } = [];

    /// <summary>
    /// Gets or sets the most played songs.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> TopSongs { get; set; } = [];

    /// <summary>
    /// Gets or sets the most played music videos.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> TopMusicVideos { get; set; } = [];

    /// <summary>
    /// Gets or sets the most played books and audiobooks.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> TopBooks { get; set; } = [];
}
