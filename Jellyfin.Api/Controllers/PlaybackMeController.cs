using System;
using System.Linq;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Extensions;
using MediaBrowser.Controller.PlaybackStats;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.PlaybackStats;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Personal playback summary of the signed in user (any user, own data only; admins may read anyone's).
/// </summary>
[Route("PlaybackStats/Me")]
[Authorize]
public class PlaybackMeController : BaseJellyfinApiController
{
    private readonly IPlaybackStatsService _statsService;
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackMeController"/> class.
    /// </summary>
    /// <param name="statsService">Instance of the <see cref="IPlaybackStatsService"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    public PlaybackMeController(IPlaybackStatsService statsService, ISessionManager sessionManager)
    {
        _statsService = statsService;
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Gets the personal summary of the current user (or of another user, for administrators).
    /// </summary>
    /// <param name="timeZone">IANA time zone of the viewer.</param>
    /// <param name="userId">Optional user; only administrators may ask for someone else.</param>
    /// <response code="200">Summary returned.</response>
    /// <response code="403">Not allowed to read another user.</response>
    /// <returns>The summary.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<PlaybackPersonalStats> Get([FromQuery] string? timeZone = null, [FromQuery] Guid? userId = null)
    {
        var callerId = User.GetUserId();
        var targetId = userId is { } id && !id.Equals(Guid.Empty) ? id : callerId;
        if (!targetId.Equals(callerId) && !User.IsInRole(UserRoles.Administrator))
        {
            return Forbid();
        }

        var stats = _statsService.GetPersonalStats(targetId, timeZone);

        // Only a count: other people's activity is not exposed to regular users.
        stats.OthersWatching = _sessionManager.Sessions
            .Where(s => s.NowPlayingItem is not null && !s.UserId.Equals(targetId))
            .Select(s => s.UserId)
            .Distinct()
            .Count();
        return Ok(stats);
    }
}
