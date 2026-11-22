#pragma warning disable CA2227 // Collection properties should be read only: plain DTOs saved and read as JSON

using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.Home;

/// <summary>
/// One title of the home page, with just what a card shows (no full item: the page asks nothing else).
/// </summary>
public class HomeCard
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the item type (Movie, Series, Episode, Audio...).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the rating (IMDb, or the community rating).</summary>
    public float? Rating { get; set; }

    /// <summary>Gets or sets the poster tag.</summary>
    public string? PrimaryTag { get; set; }

    /// <summary>Gets or sets the backdrop tag.</summary>
    public string? BackdropTag { get; set; }

    /// <summary>Gets or sets the logo tag.</summary>
    public string? LogoTag { get; set; }

    /// <summary>Gets or sets the poster aspect ratio (width / height).</summary>
    public double? AspectRatio { get; set; }

    /// <summary>Gets or sets the synopsis (only for the banner).</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the first genres (only for the banner).</summary>
    public IReadOnlyList<string>? Genres { get; set; }

    /// <summary>Gets or sets why it is shown: NewSeries, NewEpisodes, Movie, Continuing (series still on air).</summary>
    public string? Note { get; set; }

    /// <summary>Gets or sets a number for the note (new episodes).</summary>
    public int? NoteCount { get; set; }

    /// <summary>Gets or sets the library the title is in.</summary>
    public Guid? LibraryId { get; set; }
}

/// <summary>
/// A title being watched or listened to (continue, next up): landscape image and progress.
/// </summary>
public class HomeProgressCard
{
    /// <summary>Gets or sets the item to play.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the title (series name for episodes).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the second line (S01E02 · episode name, author...).</summary>
    public string? Subtitle { get; set; }

    /// <summary>Gets or sets the item type.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the progress, 0 to 100.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets the time left, in ticks.</summary>
    public long? RemainingTicks { get; set; }

    /// <summary>Gets or sets where playback stopped, in ticks (to resume with one tap).</summary>
    public long PositionTicks { get; set; }

    /// <summary>Gets or sets the item whose image is shown.</summary>
    public Guid ImageItemId { get; set; }

    /// <summary>Gets or sets the image type (Primary, Thumb, Backdrop).</summary>
    public string ImageType { get; set; } = "Primary";

    /// <summary>Gets or sets the image tag.</summary>
    public string? ImageTag { get; set; }

    /// <summary>Gets or sets the series (episodes), to open it.</summary>
    public Guid? SeriesId { get; set; }
}

