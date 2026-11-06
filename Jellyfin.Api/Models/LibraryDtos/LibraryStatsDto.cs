using System;

namespace Jellyfin.Api.Models.LibraryDtos;

/// <summary>
/// Statistics for one library (virtual folder), used by the dashboard library cards.
/// </summary>
public class LibraryStatsDto
{
    /// <summary>
    /// Gets or sets the library item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the number of media files (non folder items).
    /// </summary>
    public int FileCount { get; set; }

    /// <summary>
    /// Gets or sets the total number of items, including folders such as series or albums.
    /// </summary>
    public int ItemCount { get; set; }

    /// <summary>
    /// Gets or sets the movie count.
    /// </summary>
    public int MovieCount { get; set; }

    /// <summary>
    /// Gets or sets the series count.
    /// </summary>
    public int SeriesCount { get; set; }

    /// <summary>
    /// Gets or sets the season count.
    /// </summary>
    public int SeasonCount { get; set; }

    /// <summary>
    /// Gets or sets the episode count.
    /// </summary>
    public int EpisodeCount { get; set; }

    /// <summary>
    /// Gets or sets the album count.
    /// </summary>
    public int AlbumCount { get; set; }

    /// <summary>
    /// Gets or sets the artist count.
    /// </summary>
    public int ArtistCount { get; set; }

    /// <summary>
    /// Gets or sets the song count.
    /// </summary>
    public int SongCount { get; set; }

    /// <summary>
    /// Gets or sets the music video count.
    /// </summary>
    public int MusicVideoCount { get; set; }

    /// <summary>
    /// Gets or sets the book count.
    /// </summary>
    public int BookCount { get; set; }

    /// <summary>
    /// Gets or sets the photo count.
    /// </summary>
    public int PhotoCount { get; set; }

    /// <summary>
    /// Gets or sets the generic video count.
    /// </summary>
    public int VideoCount { get; set; }

    /// <summary>
    /// Gets or sets the collection (box set) count.
    /// </summary>
    public int BoxSetCount { get; set; }

    /// <summary>
    /// Gets or sets the total size of the media files in bytes.
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// Gets or sets the total runtime of the media in ticks.
    /// </summary>
    public long TotalRunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the date of the last library refresh.
    /// </summary>
    public DateTime? LastScanDate { get; set; }

    /// <summary>
    /// Gets or sets the date the most recent item was added.
    /// </summary>
    public DateTime? LastAddedDate { get; set; }

    /// <summary>
    /// Gets or sets the name of the most recently added item.
    /// </summary>
    public string? LastAddedName { get; set; }

    /// <summary>
    /// Gets or sets the id of the most recently added item.
    /// </summary>
    public Guid? LastAddedId { get; set; }

    /// <summary>
    /// Gets or sets when these statistics were computed.
    /// </summary>
    public DateTime ComputedAt { get; set; }
}
