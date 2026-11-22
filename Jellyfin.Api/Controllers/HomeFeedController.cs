using Jellyfin.Api.Extensions;
using MediaBrowser.Controller.Home;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Home;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The home page in one answer.
/// </summary>
[Route("HomeFeed")]
[Authorize]
public class HomeFeedController : BaseJellyfinApiController
{
    private readonly IHomeSnapshotService _home;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeFeedController"/> class.
    /// </summary>
    /// <param name="home">The home page service.</param>
    /// <param name="userManager">The user manager.</param>
    public HomeFeedController(IHomeSnapshotService home, IUserManager userManager)
    {
        _home = home;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets the home page of the current user: the part prepared by the scheduled task (banner, top 10,
    /// rows, genres) and their own rows (continue watching, next up, continue listening).
    /// </summary>
    /// <response code="200">Page returned.</response>
    /// <returns>The page.</returns>
    [HttpGet("Page")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<HomePageDto> GetPage()
    {
        var user = _userManager.GetUserById(User.GetUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        return _home.GetPage(user);
    }

    /// <summary>
    /// Gets the rows of the current user: continue watching, next up, continue listening, favorites and the
    /// music they played. Asked apart so the prepared part of the page shows first.
    /// </summary>
    /// <response code="200">Rows returned.</response>
    /// <returns>The rows.</returns>
    [HttpGet("Personal")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<HomePersonalDto> GetPersonal()
    {
        var user = _userManager.GetUserById(User.GetUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        return _home.GetPersonal(user);
    }

    /// <summary>
    /// Gets the start page of a library, prepared by the scheduled task like the home page.
    /// </summary>
    /// <param name="libraryId">The library id.</param>
    /// <response code="200">Page returned.</response>
    /// <returns>The page.</returns>
    [HttpGet("Library/{libraryId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<HomeLibraryPageDto> GetLibraryPage([FromRoute] System.Guid libraryId)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        return _home.GetLibraryPage(user, libraryId);
    }

    /// <summary>
    /// Gets the rows of the current user inside a library: continue watching, next up, favorites.
    /// </summary>
    /// <param name="libraryId">The library id.</param>
    /// <response code="200">Rows returned.</response>
    /// <returns>The rows.</returns>
    [HttpGet("Library/{libraryId}/Personal")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<HomePersonalDto> GetLibraryPersonal([FromRoute] System.Guid libraryId)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        return _home.GetLibraryPersonal(user, libraryId);
    }
}
