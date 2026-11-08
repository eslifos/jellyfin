using System;

namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// A .bak copy left next to a repaired media file.
/// </summary>
public class RepairBackupInfo
{
    /// <summary>
    /// Gets or sets the id of the library item the backup belongs to.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets a display name (movie title or "Series S01E02").
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the full path of the .bak file.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the last write time (UTC), which is when the repair happened.
    /// </summary>
    public DateTime DateModified { get; set; }
}
