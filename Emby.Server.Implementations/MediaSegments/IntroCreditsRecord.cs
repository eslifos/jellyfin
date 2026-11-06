using System;

namespace Emby.Server.Implementations.MediaSegments;

/// <summary>
/// Result of the intro / credits analysis of one item.
/// </summary>
public class IntroCreditsRecord
{
    /// <summary>
    /// Gets or sets the analyzer version that produced this record.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Gets or sets when the item was analyzed.
    /// </summary>
    public DateTime AnalyzedUtc { get; set; }

    /// <summary>
    /// Gets or sets the runtime of the item when it was analyzed (used to detect replaced files).
    /// </summary>
    public long RunTimeTicks { get; set; }

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
