namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// Status of a VobSub inspection or repair.
/// </summary>
public enum VobSubRepairStatus
{
    /// <summary>
    /// The file has no broken VobSub track.
    /// </summary>
    NotNeeded = 0,

    /// <summary>
    /// The file has broken VobSub tracks that can be repaired.
    /// </summary>
    NeedsRepair = 1,

    /// <summary>
    /// The file was repaired and replaced.
    /// </summary>
    Repaired = 2,

    /// <summary>
    /// The repair failed; the original file was not modified.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// mkvtoolnix is not installed on the server.
    /// </summary>
    ToolsMissing = 4,

    /// <summary>
    /// The item has no local MKV file.
    /// </summary>
    Unsupported = 5
}
