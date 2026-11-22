#pragma warning disable CA1819 // Properties should not return arrays: plain DTO saved and read as JSON

using System;

namespace MediaBrowser.Model.Home;

/// <summary>
/// A book of an audiobooks library, as prepared in the background (the same for every user): a standalone
/// audiobook (one file) or a book in parts (an album whose tracks are the parts).
/// </summary>
public class AudiobookEntry
{
    /// <summary>Gets or sets the book id (the audiobook or the album).</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the item kind (AudioBook, MusicAlbum, Book).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the parts in listening order (the book itself when it is one file).</summary>
    public Guid[] PartIds { get; set; } = [];

    /// <summary>Gets or sets the length of each part.</summary>
    public long[] PartTicks { get; set; } = [];

    /// <summary>Gets or sets the saga it belongs to.</summary>
    public string? Saga { get; set; }

    /// <summary>Gets or sets its number in the saga.</summary>
    public int? SagaIndex { get; set; }

    /// <summary>Gets or sets the authors.</summary>
    public string[] Authors { get; set; } = [];

    /// <summary>Gets or sets the narrators.</summary>
    public string[] Narrators { get; set; } = [];

    /// <summary>Gets or sets when it was added.</summary>
    public DateTime DateCreated { get; set; }

    /// <summary>Gets or sets the cover tag.</summary>
    public string? PrimaryTag { get; set; }

    /// <summary>Gets or sets the synopsis.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the rating.</summary>
    public float? Rating { get; set; }

    /// <summary>Gets or sets the genres.</summary>
    public string[] Genres { get; set; } = [];
}
