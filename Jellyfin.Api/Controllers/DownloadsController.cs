using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Downloads;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Downloads;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Encrypted offline downloads of phones and tablets: the devices report their progress, the dashboard follows it.
/// </summary>
[Route("Downloads")]
[Authorize]
public class DownloadsController : BaseJellyfinApiController
{
    private readonly IDownloadMonitor _monitor;
    private readonly IUserManager _userManager;
    private readonly ITranscodeManager _transcodeManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadsController"/> class.
    /// </summary>
    /// <param name="monitor">The download monitor.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="transcodeManager">The transcode manager.</param>
    public DownloadsController(IDownloadMonitor monitor, IUserManager userManager, ITranscodeManager transcodeManager)
    {
        _monitor = monitor;
        _userManager = userManager;
        _transcodeManager = transcodeManager;
    }

    /// <summary>
    /// Reports the progress of a download of the current device.
    /// </summary>
    /// <param name="report">The progress.</param>
    /// <response code="204">Saved.</response>
    /// <returns>Nothing.</returns>
    [HttpPost("Progress")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ReportProgress([FromBody] DownloadProgressReport report)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        await _monitor.ReportAsync(new DownloadProgressInfo
        {
            ItemId = report.ItemId,
            Name = report.Name,
            GroupName = report.GroupName,
            Type = report.Type,
            Quality = report.Quality,
            Status = report.Status,
            Reason = report.Reason,
            Done = report.Done,
            Total = report.Total,
            Bytes = report.Bytes,
            EstimatedBytes = report.EstimatedBytes,
            SpeedBytesPerSecond = report.SpeedBytesPerSecond,
            EtaSeconds = report.EtaSeconds,
            PlaySessionId = report.PlaySessionId,
            UserId = user.Id,
            UserName = user.Username,
            DeviceId = User.GetDeviceId() ?? string.Empty,
            DeviceName = User.GetDevice() ?? string.Empty,
            Client = User.GetClient()
        }).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets the downloads of every device (active first, then the last 24 hours), with what the server does for them.
    /// </summary>
    /// <response code="200">Downloads returned.</response>
    /// <returns>The downloads.</returns>
    [HttpGet("Monitor")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DownloadProgressInfo>> GetMonitor()
    {
        var downloads = _monitor.GetAll();
        foreach (var download in downloads)
        {
            download.Transcode = null;
            if (string.IsNullOrEmpty(download.PlaySessionId) || _transcodeManager.GetTranscodingJob(download.PlaySessionId) is not { HasExited: false } job)
            {
                continue;
            }

            var streams = job.MediaSource?.MediaStreams ?? [];
            var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var audio = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
            download.Transcode = new DownloadTranscodeInfo
            {
                CompletionPercentage = job.CompletionPercentage,
                Framerate = job.Framerate,
                BitRate = job.BitRate,
                BytesTranscoded = job.BytesTranscoded,
                SourceVideo = video is null ? null : $"{video.Codec?.ToUpperInvariant()} {video.Width}x{video.Height}",
                SourceAudio = audio is null ? null : $"{audio.Codec?.ToUpperInvariant()} {audio.Channels}ch"
            };
        }

        return Ok(downloads);
    }
}
