using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.MetaUniverses;

/// <summary>
/// A meta universe: content of every library (movies, series, music, collections) that belongs to the
/// same franchise. Built-in and automatic universes can be overridden by an administrator; custom ones are
/// created by hand.
/// </summary>
public class MetaUniverseDefinition
{
    /// <summary>
    /// Gets or sets the id ("mcu", "tmdbc-10", "custom-…").
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional description.
    /// </summary>
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets where it comes from: "builtin", "auto" or "custom".
    /// </summary>
    public string Source { get; set; } = "custom";

    /// <summary>
    /// Gets or sets a value indicating whether it is hidden from users.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Gets or sets the TMDb keywords (stored as tags) that make an item part of the universe.
    /// </summary>
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the studios that make an item part of the universe.
    /// </summary>
    public IReadOnlyList<string> Studios { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets regular expressions matched against the title or original title.
    /// </summary>
    public IReadOnlyList<string> TitlePatterns { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets TMDb collection ids whose movies belong to the universe.
    /// </summary>
    public IReadOnlyList<string> TmdbCollectionIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets items added by hand.
    /// </summary>
    public IReadOnlyList<Guid> IncludeIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets items removed by hand.
    /// </summary>
    public IReadOnlyList<Guid> ExcludeIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets the in-universe (chronological) order; items not listed follow by release date.
    /// </summary>
    public IReadOnlyList<Guid> ChronologicalOrder { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets the item whose backdrop is used for the banner (optional).
    /// </summary>
    public Guid? BackdropItemId { get; set; }
}
