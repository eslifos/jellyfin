namespace MediaBrowser.Model.PlaybackStats;

/// <summary>
/// The kinds of library a recorded play can belong to.
/// </summary>
public static class PlaybackCategory
{
    /// <summary>A series episode.</summary>
    public const string Episode = "Episode";

    /// <summary>A movie.</summary>
    public const string Movie = "Movie";

    /// <summary>A song.</summary>
    public const string Music = "Music";

    /// <summary>A music video.</summary>
    public const string MusicVideo = "MusicVideo";

    /// <summary>A book or audiobook, in one file or in parts.</summary>
    public const string Book = "Book";

    /// <summary>Anything else: live TV, home videos, trailers...</summary>
    public const string Other = "Other";

    /// <summary>
    /// Gets the category of a play from its item type, for plays whose item is no longer in the library.
    /// </summary>
    /// <param name="itemType">The item type.</param>
    /// <returns>The category.</returns>
    public static string FromItemType(string? itemType) => itemType switch
    {
        "Episode" => Episode,
        "Movie" => Movie,
        "Audio" => Music,
        "MusicVideo" => MusicVideo,
        "AudioBook" or "Book" => Book,
        _ => Other
    };

    /// <summary>
    /// Tells whether a text is one of the categories.
    /// </summary>
    /// <param name="category">The text.</param>
    /// <returns>Whether it is a category.</returns>
    public static bool IsKnown(string? category)
        => category is Episode or Movie or Music or MusicVideo or Book or Other;
}
