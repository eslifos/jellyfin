using System.IO;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Where the extraction of a subtitle inside a video file stands.
/// </summary>
public enum SubtitleExtractionState
{
    /// <summary>The subtitle is not read out of the video file (external, image, or extraction disabled).</summary>
    NotNeeded,

    /// <summary>The video file is being read; part of the subtitle may already be there.</summary>
    Extracting,

    /// <summary>The whole subtitle is ready.</summary>
    Complete,

    /// <summary>The last extraction failed (it can be tried again).</summary>
    Failed
}

/// <summary>
/// The part of a subtitle already extracted from the video file.
/// </summary>
/// <param name="Content">The subtitle so far, or null when there is nothing to show yet.</param>
/// <param name="State">Where the extraction stands.</param>
public sealed record PartialSubtitles(Stream? Content, SubtitleExtractionState State);
