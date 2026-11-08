using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.PlaybackStats;

/// <summary>
/// Playback summary of the signed in user (shown on their home screen).
/// </summary>
public class PlaybackPersonalStats
{
    /// <summary>
    /// Gets or sets the seconds watched in the last 30 days.
    /// </summary>
    public long WatchedSeconds30 { get; set; }

    /// <summary>
    /// Gets or sets the plays of the last 30 days.
    /// </summary>
    public int Plays30 { get; set; }

    /// <summary>
    /// Gets or sets the plays finished in the last 30 days.
    /// </summary>
    public int Finished30 { get; set; }

    /// <summary>
    /// Gets or sets the number of consecutive days, up to today or yesterday, with at least one play.
    /// </summary>
    public int CurrentStreakDays { get; set; }

    /// <summary>
    /// Gets or sets the longest streak of consecutive days ever.
    /// </summary>
    public int BestStreakDays { get; set; }

    /// <summary>
    /// Gets or sets the series of the biggest binge (most episodes of one series in a day).
    /// </summary>
    public Guid? BingeSeriesId { get; set; }

    /// <summary>
    /// Gets or sets the series name of the biggest binge.
    /// </summary>
    public string? BingeSeriesName { get; set; }

    /// <summary>
    /// Gets or sets the episodes watched in the biggest binge.
    /// </summary>
    public int BingeEpisodes { get; set; }

    /// <summary>
    /// Gets or sets the local date of the biggest binge (yyyy-MM-dd).
    /// </summary>
    public string? BingeDate { get; set; }

    /// <summary>
    /// Gets or sets the last play of the user.
    /// </summary>
    public PlaybackHistoryEntry? LastPlay { get; set; }

    /// <summary>
    /// Gets or sets how many other people are watching something right now.
    /// </summary>
    public int OthersWatching { get; set; }

    /// <summary>
    /// Gets or sets the plays ever.
    /// </summary>
    public int TotalPlays { get; set; }

    /// <summary>
    /// Gets or sets the seconds watched ever.
    /// </summary>
    public long TotalWatchedSeconds { get; set; }

    /// <summary>
    /// Gets or sets the seconds of episodes watched ever.
    /// </summary>
    public long TotalEpisodeSeconds { get; set; }

    /// <summary>
    /// Gets or sets the seconds of movies watched ever.
    /// </summary>
    public long TotalMovieSeconds { get; set; }

    /// <summary>
    /// Gets or sets the distinct episodes finished ever.
    /// </summary>
    public int EpisodesFinished { get; set; }

    /// <summary>
    /// Gets or sets the distinct movies finished ever.
    /// </summary>
    public int MoviesFinished { get; set; }

    /// <summary>
    /// Gets or sets the distinct days with at least one play.
    /// </summary>
    public int ActiveDays { get; set; }

    /// <summary>
    /// Gets or sets the first recorded play.
    /// </summary>
    public DateTime? FirstPlayUtc { get; set; }

    /// <summary>
    /// Gets or sets the last 30 local days.
    /// </summary>
    public IReadOnlyList<PlaybackStatsDay> Daily { get; set; } = Array.Empty<PlaybackStatsDay>();

    /// <summary>
    /// Gets or sets the plays started per local hour (0-23), all time.
    /// </summary>
    public IReadOnlyList<int> PlaysByHour { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Gets or sets the seconds watched per weekday (Monday first), all time.
    /// </summary>
    public IReadOnlyList<long> SecondsByWeekday { get; set; } = Array.Empty<long>();

    /// <summary>
    /// Gets or sets the most watched series, all time.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> TopSeries { get; set; } = Array.Empty<PlaybackStatsRank>();

    /// <summary>
    /// Gets or sets the most watched movies, all time.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> TopMovies { get; set; } = Array.Empty<PlaybackStatsRank>();

    /// <summary>
    /// Gets or sets the most used apps, all time.
    /// </summary>
    public IReadOnlyList<PlaybackStatsRank> TopClients { get; set; } = Array.Empty<PlaybackStatsRank>();

    /// <summary>
    /// Gets or sets the most recent plays.
    /// </summary>
    public IReadOnlyList<PlaybackHistoryEntry> Recent { get; set; } = Array.Empty<PlaybackHistoryEntry>();
}
