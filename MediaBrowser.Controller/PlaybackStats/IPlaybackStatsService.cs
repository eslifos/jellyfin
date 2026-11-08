using System;
using MediaBrowser.Model.PlaybackStats;

namespace MediaBrowser.Controller.PlaybackStats;

/// <summary>
/// Reads the recorded playback history (Tautulli style statistics).
/// </summary>
public interface IPlaybackStatsService
{
    /// <summary>
    /// Gets the statistics of the last <paramref name="days"/> days.
    /// </summary>
    /// <param name="days">Number of days (1-3650).</param>
    /// <param name="timeZone">IANA time zone used to group by day / hour, or null for the server zone.</param>
    /// <returns>The overview.</returns>
    PlaybackStatsOverview GetOverview(int days, string? timeZone);

    /// <summary>
    /// Gets a page of the playback history, newest first.
    /// </summary>
    /// <param name="startIndex">Index of the first entry.</param>
    /// <param name="limit">Maximum entries.</param>
    /// <param name="userId">Only this user.</param>
    /// <param name="itemType">Only this kind: Episode, Movie or Other.</param>
    /// <param name="searchTerm">Text contained in the title, series, user, client or device.</param>
    /// <returns>The page.</returns>
    PlaybackHistoryResult GetHistory(int startIndex, int limit, Guid? userId, string? itemType, string? searchTerm);

    /// <summary>
    /// Gets the playback summary of every user that played something.
    /// </summary>
    /// <param name="timeZone">IANA time zone used to split the last 30 days, or null for the server zone.</param>
    /// <returns>One entry per user.</returns>
    System.Collections.Generic.IReadOnlyList<PlaybackUserStats> GetUserStats(string? timeZone);

    /// <summary>
    /// Gets the personal summary of one user (streaks, biggest binge, last 30 days).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="timeZone">IANA time zone of the viewer, or null for the server zone.</param>
    /// <returns>The summary (OthersWatching is filled by the caller).</returns>
    PlaybackPersonalStats GetPersonalStats(Guid userId, string? timeZone);
}
