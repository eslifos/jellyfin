using System.Collections.Generic;

namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// Outcome of inspecting or repairing the VobSub (DVD) subtitle tracks of a file.
/// </summary>
public class VobSubRepairResult
{
    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public VobSubRepairStatus Status { get; set; }

    /// <summary>
    /// Gets or sets a human readable detail (error output, etc).
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the file path.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the ids of the broken VobSub tracks.
    /// </summary>
    public IReadOnlyList<int> BrokenTracks { get; set; } = [];

    /// <summary>
    /// Gets or sets the frame size written to the repaired tracks (for example 720x480).
    /// </summary>
    public string? Size { get; set; }

    /// <summary>
    /// Gets or sets the path of the original file kept as backup, if any.
    /// </summary>
    public string? BackupPath { get; set; }
}
