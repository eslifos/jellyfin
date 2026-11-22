using System;

namespace MediaBrowser.Model.Downloads;

/// <summary>
/// What a phone or tablet tells the server about one of its downloads (encrypted offline copies).
/// </summary>
public class DownloadProgressReport
{
    /// <summary>Gets or sets the downloaded item.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the name of the item.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the series, album or audiobook it belongs to.</summary>
    public string? GroupName { get; set; }

    /// <summary>Gets or sets the item kind.</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the chosen quality (original, high, optimal, saver).</summary>
    public string? Quality { get; set; }

    /// <summary>Gets or sets the state: queued, preparing, downloading, paused, done, error, removed.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets why it waits or failed: space, wifi, network, server.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets the pieces already saved on the device.</summary>
    public int Done { get; set; }

    /// <summary>Gets or sets the pieces of the whole item.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the bytes already saved.</summary>
    public long Bytes { get; set; }

    /// <summary>Gets or sets the expected size.</summary>
    public long EstimatedBytes { get; set; }

    /// <summary>Gets or sets the current speed.</summary>
    public double SpeedBytesPerSecond { get; set; }

    /// <summary>Gets or sets the time left, in seconds.</summary>
    public double? EtaSeconds { get; set; }

    /// <summary>Gets or sets the play session of the conversion on the server.</summary>
    public string? PlaySessionId { get; set; }
}

/// <summary>
/// A download as the dashboard shows it: the report of the device plus who, where and what the server does.
/// </summary>
public class DownloadProgressInfo : DownloadProgressReport
{
    /// <summary>Gets or sets the user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user name.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the device id.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the device name.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Gets or sets the app.</summary>
    public string? Client { get; set; }

    /// <summary>Gets or sets when the download started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Gets or sets the last report of the device.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Gets or sets when it finished.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets what the server is doing to prepare it (null when it is not converting).</summary>
    public DownloadTranscodeInfo? Transcode { get; set; }
}

/// <summary>
/// The conversion the server runs for a download.
/// </summary>
public class DownloadTranscodeInfo
{
    /// <summary>Gets or sets how much of the item the server already converted, 0 to 100.</summary>
    public double? CompletionPercentage { get; set; }

    /// <summary>Gets or sets the frames per second the server converts at.</summary>
    public float? Framerate { get; set; }

    /// <summary>Gets or sets the bitrate of the converted copy.</summary>
    public int? BitRate { get; set; }

    /// <summary>Gets or sets the bytes converted.</summary>
    public long? BytesTranscoded { get; set; }

    /// <summary>Gets or sets the video of the original file (codec and size).</summary>
    public string? SourceVideo { get; set; }

    /// <summary>Gets or sets the audio of the original file.</summary>
    public string? SourceAudio { get; set; }
}
