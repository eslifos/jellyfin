using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.MetaUniverses;

/// <summary>
/// A meta universe as shown in the list of universes (only content visible to the user is counted).
/// </summary>
public class MetaUniverseSummary
{
    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets the source: "builtin", "auto" or "custom".
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether it is hidden (only returned to administrators).
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Gets or sets the number of movies.
    /// </summary>
    public int MovieCount { get; set; }

    /// <summary>
    /// Gets or sets the number of series.
    /// </summary>
    public int SeriesCount { get; set; }

    /// <summary>
    /// Gets or sets the number of music albums (soundtracks).
    /// </summary>
    public int AlbumCount { get; set; }

    /// <summary>
    /// Gets or sets the number of collections.
    /// </summary>
    public int CollectionCount { get; set; }

    /// <summary>
    /// Gets or sets the first and last release year.
    /// </summary>
    public int? FirstYear { get; set; }

    /// <summary>
    /// Gets or sets the last release year.
    /// </summary>
    public int? LastYear { get; set; }

    /// <summary>
    /// Gets or sets items with a backdrop to use as artwork (banner first).
    /// </summary>
    public IReadOnlyList<Guid> BackdropItemIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets items with a poster, the most popular first.
    /// </summary>
    public IReadOnlyList<Guid> PosterItemIds { get; set; } = Array.Empty<Guid>();
}
