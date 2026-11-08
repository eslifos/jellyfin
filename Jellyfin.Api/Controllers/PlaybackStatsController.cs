using System;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.PlaybackStats;
using MediaBrowser.Model.PlaybackStats;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Playback statistics and history (Tautulli style).
/// </summary>
[Route("PlaybackStats")]
[Authorize(Policy = Policies.RequiresElevation)]
public class PlaybackStatsController : BaseJellyfinApiController
{
    private readonly IPlaybackStatsService _statsService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStatsController"/> class.
    /// </summary>
    /// <param name="statsService">Instance of the <see cref="IPlaybackStatsService"/> interface.</param>
    public PlaybackStatsController(IPlaybackStatsService statsService)
    {
        _statsService = statsService;
    }

    /// <summary>
    /// Gets the statistics of the last days, compared with the period before.
    /// </summary>
    /// <param name="days">Number of days (default 30).</param>
    /// <param name="timeZone">IANA time zone of the viewer, used to group by day and hour.</param>
    /// <response code="200">Statistics returned.</response>
    /// <returns>The overview.</returns>
    [HttpGet("Overview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PlaybackStatsOverview> GetOverview([FromQuery] int days = 30, [FromQuery] string? timeZone = null)
        => Ok(_statsService.GetOverview(days, timeZone));

    /// <summary>
    /// Gets the playback history, newest first.
    /// </summary>
    /// <param name="startIndex">Index of the first entry.</param>
    /// <param name="limit">Maximum entries (default 50, max 500).</param>
    /// <param name="userId">Only this user.</param>
    /// <param name="itemType">Episode, Movie or Other.</param>
    /// <param name="searchTerm">Text to search in title, series, user, client or device.</param>
    /// <response code="200">History returned.</response>
    /// <returns>A page of the history.</returns>
    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PlaybackHistoryResult> GetHistory(
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? itemType = null,
        [FromQuery] string? searchTerm = null)
        => Ok(_statsService.GetHistory(startIndex, limit, userId, itemType, searchTerm));

    /// <summary>
    /// Gets the playback summary of every user.
    /// </summary>
    /// <param name="timeZone">IANA time zone of the viewer.</param>
    /// <response code="200">Summaries returned.</response>
    /// <returns>One entry per user that played something.</returns>
    [HttpGet("Users")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<System.Collections.Generic.IReadOnlyList<PlaybackUserStats>> GetUsers([FromQuery] string? timeZone = null)
        => Ok(_statsService.GetUserStats(timeZone));
}
