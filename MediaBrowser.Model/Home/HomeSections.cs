#pragma warning disable CA2227 // Collection properties should be read only: plain DTOs saved and read as JSON

using System;
using System.Collections.Generic;
using MediaBrowser.Model.Discovery;

namespace MediaBrowser.Model.Home;

/// <summary>
/// A row of titles.
/// </summary>
public class HomeShelf
{
    /// <summary>Gets or sets what the row is (top10movies, latest, releases, toprated, gems, onair...).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets how the cards look: poster, rank (top 10), square (albums), round (artists), wide.</summary>
    public string Shape { get; set; } = "poster";

    /// <summary>Gets or sets the titles.</summary>
    public List<HomeCard> Items { get; set; } = [];
}

/// <summary>
/// The rows of one library, shown together under its name.
/// </summary>
public class HomeBlock
{
    /// <summary>Gets or sets the library id.</summary>
    public Guid LibraryId { get; set; }

    /// <summary>Gets or sets the library name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of library (movies, tvshows, music, books...; empty for mixed).</summary>
    public string? CollectionType { get; set; }

    /// <summary>Gets or sets the rows.</summary>
    public List<HomeShelf> Rows { get; set; } = [];

    /// <summary>Gets or sets the "what do we watch today?" picks of the library (well rated, with a backdrop).</summary>
    public List<HomeCard> Spotlight { get; set; } = [];

    /// <summary>Gets or sets the banner of the library page (its top 10 with a backdrop).</summary>
    public List<HomeCard> Hero { get; set; } = [];

    /// <summary>Gets or sets rows shown only on the library page (genres).</summary>
    public List<HomeShelf> Extra { get; set; } = [];

    /// <summary>Gets or sets the episodes and saga movies of the library coming out soon.</summary>
    public List<UpcomingRelease> Upcoming { get; set; } = [];
}

/// <summary>
/// The start page of one library, prepared like the home page.
/// </summary>
public class HomeLibraryPageDto
{
    /// <summary>Gets or sets a value indicating whether it is prepared (false only the first time).</summary>
    public bool Ready { get; set; }

    /// <summary>Gets or sets when it was prepared.</summary>
    public DateTime? Built { get; set; }

    /// <summary>Gets or sets the library with all its rows (null when the user cannot open it).</summary>
    public HomeBlock? Block { get; set; }
}

/// <summary>
/// A genre with how many titles it has.
/// </summary>
public class HomeGenre
{
    /// <summary>Gets or sets the genre id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets how many movies and series it has.</summary>
    public int Count { get; set; }
}

/// <summary>
/// A library of the user (order of the sections, links).
/// </summary>
public class HomeLibrary
{
    /// <summary>Gets or sets the library id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind (movies, tvshows, music, books...).</summary>
    public string? CollectionType { get; set; }
}

/// <summary>
/// What is the same for everybody, prepared by the scheduled task and kept on disk.
/// </summary>
public class HomeSnapshot
{
    /// <summary>Gets or sets the version of the format (an older file is shown while a new one is prepared).</summary>
    public int Version { get; set; }

    /// <summary>Gets or sets when it was prepared.</summary>
    public DateTime Built { get; set; }

    /// <summary>Gets or sets when the genres were counted (heavy: once a day).</summary>
    public DateTime GenresBuilt { get; set; }

    /// <summary>Gets or sets the banner titles.</summary>
    public List<HomeCard> Hero { get; set; } = [];

    /// <summary>Gets or sets one block of rows per library.</summary>
    public List<HomeBlock> Blocks { get; set; } = [];

    /// <summary>Gets or sets the episodes and saga movies coming out soon.</summary>
    public List<UpcomingRelease> Upcoming { get; set; } = [];

    /// <summary>Gets or sets the genres with the most titles.</summary>
    public List<HomeGenre> Genres { get; set; } = [];
}

/// <summary>
/// The shared part of the home page of a user (only what they can see), in one answer.
/// </summary>
public class HomePageDto
{
    /// <summary>Gets or sets a value indicating whether it is prepared (false only the first time).</summary>
    public bool Ready { get; set; }

    /// <summary>Gets or sets when it was prepared.</summary>
    public DateTime? Built { get; set; }

    /// <summary>Gets or sets the libraries of the user, in the menu order.</summary>
    public List<HomeLibrary> Libraries { get; set; } = [];

    /// <summary>Gets or sets the banner.</summary>
    public List<HomeCard> Hero { get; set; } = [];

    /// <summary>Gets or sets the library blocks.</summary>
    public List<HomeBlock> Blocks { get; set; } = [];

    /// <summary>Gets or sets the "what do we watch today?" picks of all the libraries of the user.</summary>
    public List<HomeCard> Spotlight { get; set; } = [];

    /// <summary>Gets or sets the upcoming releases.</summary>
    public List<UpcomingRelease> Upcoming { get; set; } = [];

    /// <summary>Gets or sets the genres.</summary>
    public List<HomeGenre> Genres { get; set; } = [];
}

/// <summary>
/// The rows that belong to each user (asked apart, so the rest of the page shows first).
/// </summary>
public class HomePersonalDto
{
    /// <summary>Gets or sets continue watching.</summary>
    public List<HomeProgressCard> Continue { get; set; } = [];

    /// <summary>Gets or sets the next episodes.</summary>
    public List<HomeProgressCard> NextUp { get; set; } = [];

    /// <summary>Gets or sets continue listening (audiobooks, music, books).</summary>
    public List<HomeProgressCard> Listening { get; set; } = [];

    /// <summary>Gets or sets the favorites.</summary>
    public List<HomeCard> Favorites { get; set; } = [];

    /// <summary>Gets or sets rows of the user inside library blocks (music played, books being read).</summary>
    public List<HomeBlock> LibraryRows { get; set; } = [];
}
