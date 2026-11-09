using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.MetaUniverses;

/// <summary>
/// An actor who appears in several titles of a meta universe.
/// </summary>
public class MetaUniversePerson
{
    /// <summary>
    /// Gets or sets the person item id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the characters played (most frequent first).
    /// </summary>
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the number of titles of the universe they appear in.
    /// </summary>
    public int Appearances { get; set; }
}
