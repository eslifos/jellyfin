using System.Collections.Generic;

namespace MediaBrowser.Model.PlaybackStats;

/// <summary>
/// A page of the playback history.
/// </summary>
public class PlaybackHistoryResult
{
    /// <summary>
    /// Gets or sets the entries of the page.
    /// </summary>
    public IReadOnlyList<PlaybackHistoryEntry> Items { get; set; } = [];

    /// <summary>
    /// Gets or sets the total number of entries matching the filters.
    /// </summary>
    public int TotalRecordCount { get; set; }
}
