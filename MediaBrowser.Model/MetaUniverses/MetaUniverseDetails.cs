using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.MetaUniverses;

/// <summary>
/// Everything of one meta universe that the user can see.
/// </summary>
public class MetaUniverseDetails
{
    /// <summary>
    /// Gets or sets the summary.
    /// </summary>
    public MetaUniverseSummary Summary { get; set; } = new();

    /// <summary>
    /// Gets or sets the definition (rules, manual changes), for the editor.
    /// </summary>
    public MetaUniverseDefinition Definition { get; set; } = new();

    /// <summary>
    /// Gets or sets the movies and series in release order.
    /// </summary>
    public IReadOnlyList<Guid> ReleaseOrder { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets the movies and series in chronological (in-universe) order.
    /// </summary>
    public IReadOnlyList<Guid> ChronologicalOrder { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets a value indicating whether a chronological order was set by hand.
    /// </summary>
    public bool HasChronology { get; set; }

    /// <summary>
    /// Gets or sets the movies.
    /// </summary>
    public IReadOnlyList<Guid> MovieIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets the series.
    /// </summary>
    public IReadOnlyList<Guid> SeriesIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets the music albums (soundtracks).
    /// </summary>
    public IReadOnlyList<Guid> AlbumIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets the collections.
    /// </summary>
    public IReadOnlyList<Guid> CollectionIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets other content (books, music videos…).
    /// </summary>
    public IReadOnlyList<Guid> OtherIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets the actors who appear in several titles of the universe.
    /// </summary>
    public IReadOnlyList<MetaUniversePerson> People { get; set; } = Array.Empty<MetaUniversePerson>();
}
