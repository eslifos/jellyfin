using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Discovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Home banner picks and upcoming releases.
/// </summary>
[Route("Discovery")]
[Authorize]
public class DiscoveryController : BaseJellyfinApiController
{
    private readonly IDiscoveryService _discovery;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryController"/> class.
    /// </summary>
    /// <param name="discovery">The discovery service.</param>
    /// <param name="userManager">The user manager.</param>
    public DiscoveryController(IDiscoveryService discovery, IUserManager userManager)
    {
        _discovery = discovery;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets the most popular releases of this year or of the last 6 months (movies, series, anime).
    /// </summary>
    /// <param name="limit">Maximum number of titles.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Picks returned.</response>
    /// <returns>The picks, the most popular first.</returns>
    [HttpGet("Hero")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<HeroPick>>> GetHero([FromQuery] int limit = 10, CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        return Ok(await _discovery.GetHeroAsync(user, limit, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Gets upcoming episodes of the library's series and upcoming movies of its sagas (from TMDb).
    /// </summary>
    /// <param name="from">First date (default today).</param>
    /// <param name="to">Last date (default in 3 months, at most one year after the first date).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Releases returned.</response>
    /// <returns>The releases by date.</returns>
    [HttpGet("Upcoming")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UpcomingRelease>>> GetUpcoming([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        var start = (from ?? DateTime.UtcNow).ToUniversalTime();
        var end = (to ?? start.AddMonths(3)).ToUniversalTime();
        if (end < start || end > start.AddYears(1))
        {
            end = start.AddYears(1);
        }

        return Ok(await _discovery.GetUpcomingAsync(user, start, end, cancellationToken).ConfigureAwait(false));
    }
}
