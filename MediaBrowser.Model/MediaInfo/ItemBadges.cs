using System;

namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// Poster overlay info of an item (resolution, HDR, audio, source, ratings...).
/// Series and seasons get the most common values of their episodes.
/// </summary>
public class ItemBadges
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the resolution label: 4K, 1080P, 720P or SD.
    /// </summary>
    public string? Resolution { get; set; }

    /// <summary>
    /// Gets or sets the dynamic range label: DV, HDR10+, HDR or HLG.
    /// </summary>
    public string? Hdr { get; set; }

    /// <summary>
    /// Gets or sets the audio label, e.g. "TrueHD Atmos", "DD+", "DTS:X".
    /// </summary>
    public string? Audio { get; set; }

    /// <summary>
    /// Gets or sets the source: REMUX, BLU-RAY, WEB, HDTV or DVD (from the file name).
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets the edition, e.g. "Extended Edition" (from the file name).
    /// </summary>
    public string? Edition { get; set; }

    /// <summary>
    /// Gets or sets the number of distinct audio languages.
    /// </summary>
    public int AudioLanguages { get; set; }

    /// <summary>
    /// Gets or sets the streaming network/studio key (netflix, amazon, disney, hbo, apple, hulu, paramount, peacock).
    /// </summary>
    public string? Network { get; set; }

    /// <summary>
    /// Gets or sets the community rating (0-10).
    /// </summary>
    public float? CommunityRating { get; set; }

    /// <summary>
    /// Gets or sets the critic rating (0-100, Rotten Tomatoes).
    /// </summary>
    public float? CriticRating { get; set; }

    /// <summary>
    /// Gets or sets the IMDb id, when known.
    /// </summary>
    public string? ImdbId { get; set; }

    /// <summary>
    /// Gets or sets "S01E01" for episodes.
    /// </summary>
    public string? EpisodeCode { get; set; }

    /// <summary>
    /// Gets or sets the runtime in minutes (movies and episodes).
    /// </summary>
    public int? RuntimeMinutes { get; set; }
}
