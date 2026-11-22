using System;
using System.Collections.Generic;

namespace Emby.Server.Implementations.Audiobooks;

/// <summary>
/// An audiobook (or book) found in an online catalog, with the fields every source can give.
/// </summary>
public sealed class AudiobookMatch
{
    /// <summary>Gets or sets the catalog it comes from (see <see cref="AudiobookSources"/>).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the id in that catalog (ASIN for Audible).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the subtitle.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Gets or sets the authors.</summary>
    public IReadOnlyList<string> Authors { get; set; } = [];

    /// <summary>Gets or sets the narrators.</summary>
    public IReadOnlyList<string> Narrators { get; set; } = [];

    /// <summary>Gets or sets the publisher.</summary>
    public string? Publisher { get; set; }

    /// <summary>Gets or sets the release date.</summary>
    public DateTime? ReleaseDate { get; set; }

    /// <summary>Gets or sets the synopsis, as plain text.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the genres.</summary>
    public IReadOnlyList<string> Genres { get; set; } = [];

    /// <summary>Gets or sets the saga the book belongs to.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the number of the book in its saga.</summary>
    public int? SeriesIndex { get; set; }

    /// <summary>Gets or sets the largest cover available.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Gets or sets the side of the cover in pixels, when known.</summary>
    public int? ImageSize { get; set; }

    /// <summary>Gets or sets the rating, from 0 to 10.</summary>
    public float? Rating { get; set; }

    /// <summary>Gets or sets the store it was found in ("us", "es"…), a hint of the Spanish variant.</summary>
    public string? Region { get; set; }

    /// <summary>Gets or sets the language name or code as the catalog gives it.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the ISBN.</summary>
    public string? Isbn { get; set; }

    /// <summary>Gets or sets the length in minutes.</summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>Gets or sets how well it matches the title and author that were looked up (0 to 1).</summary>
    public double Score { get; set; }
}
