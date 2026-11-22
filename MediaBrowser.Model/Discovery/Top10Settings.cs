using System;

namespace MediaBrowser.Model.Discovery;

/// <summary>
/// How the top 10 rows (home and movie libraries) are built: automatically from what people watch
/// right now (TMDb), or by hand with titles picked by the administrator.
/// </summary>
public class Top10Settings
{
    /// <summary>
    /// Gets or sets a value indicating whether the movies top 10 is picked by hand.
    /// </summary>
    public bool MoviesManual { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the series top 10 is picked by hand.
    /// </summary>
    public bool SeriesManual { get; set; }

    /// <summary>
    /// Gets or sets the movies picked by hand, in ranking order (at most 10).
    /// </summary>
    public Guid[] Movies { get; set; } = [];

    /// <summary>
    /// Gets or sets the series picked by hand, in ranking order (at most 10).
    /// </summary>
    public Guid[] Series { get; set; } = [];
}
