using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.Discovery;

/// <summary>
/// The rows of the start screen of a movie library.
/// </summary>
public class LibraryDiscovery
{
    /// <summary>
    /// Gets or sets the 10 most watched movies right now (TMDb trending and popular) that are in the library.
    /// </summary>
    public IReadOnlyList<Guid> Top10 { get; set; } = [];

    /// <summary>
    /// Gets or sets the library movies that are trending on TMDb today and this week.
    /// </summary>
    public IReadOnlyList<Guid> Trending { get; set; } = [];

    /// <summary>
    /// Gets or sets the best rated movies of the library (IMDb weighted rating).
    /// </summary>
    public IReadOnlyList<Guid> TopRated { get; set; } = [];

    /// <summary>
    /// Gets or sets well rated, little known movies the user has not watched.
    /// </summary>
    public IReadOnlyList<Guid> HiddenGems { get; set; } = [];

    /// <summary>
    /// Gets or sets the great classics (up to 1990) of the library.
    /// </summary>
    public IReadOnlyList<Guid> Classics { get; set; } = [];

    /// <summary>
    /// Gets or sets the upcoming movies in theaters (TMDb) not in the library yet.
    /// </summary>
    public IReadOnlyList<UpcomingRelease> Upcoming { get; set; } = [];

    /// <summary>
    /// Gets or sets the movies in theaters now (TMDb); RelatedItemId is set when the library has it.
    /// </summary>
    public IReadOnlyList<UpcomingRelease> NowPlaying { get; set; } = [];
}
