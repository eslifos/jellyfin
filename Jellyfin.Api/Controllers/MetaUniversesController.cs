using System.Collections.Generic;
using Jellyfin.Api.Extensions;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MetaUniverses;
using MediaBrowser.Model.MetaUniverses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Meta universes: franchises that cross libraries (movies, series, soundtracks, collections).
/// </summary>
[Route("MetaUniverses")]
[Authorize]
public class MetaUniversesController : BaseJellyfinApiController
{
    private readonly IMetaUniverseService _service;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetaUniversesController"/> class.
    /// </summary>
    /// <param name="service">The meta universe service.</param>
    /// <param name="userManager">The user manager.</param>
    public MetaUniversesController(IMetaUniverseService service, IUserManager userManager)
    {
        _service = service;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets the universes with content visible to the current user.
    /// </summary>
    /// <param name="includeHidden">Include hidden and empty universes (administrators only).</param>
    /// <response code="200">Universes returned.</response>
    /// <returns>The universes.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MetaUniverseSummary>> GetUniverses([FromQuery] bool includeHidden = false)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        return Ok(_service.GetUniverses(user, includeHidden && User.IsInRole(Constants.UserRoles.Administrator)));
    }

    /// <summary>
    /// Gets one universe.
    /// </summary>
    /// <param name="id">The universe id.</param>
    /// <response code="200">Universe returned.</response>
    /// <response code="404">Universe not found.</response>
    /// <returns>The universe.</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<MetaUniverseDetails> GetUniverse([FromRoute] string id)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        var universe = _service.GetUniverse(id, user);
        if (universe is null || (universe.Definition.Hidden && !User.IsInRole(Constants.UserRoles.Administrator)))
        {
            return NotFound();
        }

        return universe;
    }

    /// <summary>
    /// Creates a custom universe (empty id) or saves changes to an existing one.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <response code="200">Universe saved.</response>
    /// <returns>The stored definition.</returns>
    [HttpPost]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<MetaUniverseDefinition> Save([FromBody] MetaUniverseDefinition definition)
    {
        try
        {
            return _service.Save(definition);
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Deletes a custom universe, or resets a built-in / automatic one to its defaults.
    /// </summary>
    /// <param name="id">The universe id.</param>
    /// <response code="204">Removed.</response>
    /// <response code="404">Nothing to remove.</response>
    /// <returns>No content.</returns>
    [HttpDelete("{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Delete([FromRoute] string id)
        => _service.Delete(id) ? NoContent() : NotFound();

    /// <summary>
    /// Rebuilds the universes from the libraries now.
    /// </summary>
    /// <response code="204">Rebuild requested.</response>
    /// <returns>No content.</returns>
    [HttpPost("Refresh")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Refresh()
    {
        _service.Invalidate();
        return NoContent();
    }
}
