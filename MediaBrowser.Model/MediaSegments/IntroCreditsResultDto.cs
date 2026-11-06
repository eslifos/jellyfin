using System;

namespace MediaBrowser.Model.MediaSegments;

/// <summary>
/// Intro / credits detection result of one movie or episode.
/// </summary>
public class IntroCreditsResultDto
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the series name (episodes only).
    /// </summary>
    public string? SeriesName { get; set; }

    /// <summary>
    /// Gets or sets the season number (episodes only).
    /// </summary>
    public int? ParentIndexNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number (episodes only).
    /// </summary>
    public int? IndexNumber { get; set; }

    /// <summary>
    /// Gets or sets the runtime of the item.
    /// </summary>
    public long RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the item was analyzed.
    /// </summary>
    public bool IsAnalyzed { get; set; }

    /// <summary>
    /// Gets or sets when the item was analyzed.
    /// </summary>
    public DateTime? AnalyzedUtc { get; set; }

    /// <summary>
    /// Gets or sets the intro start.
    /// </summary>
    public long? IntroStartTicks { get; set; }

    /// <summary>
    /// Gets or sets the intro end.
    /// </summary>
    public long? IntroEndTicks { get; set; }

    /// <summary>
    /// Gets or sets how the intro was found (Chapter, Fingerprint).
    /// </summary>
    public string? IntroSource { get; set; }

    /// <summary>
    /// Gets or sets the credits start.
    /// </summary>
    public long? CreditsStartTicks { get; set; }

    /// <summary>
    /// Gets or sets the credits end.
    /// </summary>
    public long? CreditsEndTicks { get; set; }

    /// <summary>
    /// Gets or sets how the credits were found (Chapter, Fingerprint, BlackFrames).
    /// </summary>
    public string? CreditsSource { get; set; }
}
