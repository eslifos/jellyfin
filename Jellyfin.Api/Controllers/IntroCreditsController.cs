using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// On demand intro / credits detection.
/// </summary>
[Route("IntroCredits")]
[Authorize(Policy = Policies.RequiresElevation)]
public class IntroCreditsController : BaseJellyfinApiController
{
    private readonly ILibraryManager _libraryManager;
    private readonly IIntroCreditsService _introCreditsService;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroCreditsController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="introCreditsService">Instance of the <see cref="IIntroCreditsService"/> interface.</param>
    public IntroCreditsController(ILibraryManager libraryManager, IIntroCreditsService introCreditsService)
    {
        _libraryManager = libraryManager;
        _introCreditsService = introCreditsService;
    }

    /// <summary>
    /// Gets the stored intro / credits results of a movie, episode, season or series.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="200">Results returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>The results.</returns>
    [HttpGet("{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IEnumerable<IntroCreditsResultDto>> GetResults([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        IEnumerable<IntroCreditsResultDto> results = _introCreditsService.GetResults(item);
        return Ok(results);
    }

    /// <summary>
    /// Detects intro / credits of a movie, episode, season or series right now.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Detection finished.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>The new results.</returns>
    [HttpPost("{itemId}/Detect")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<IntroCreditsResultDto>>> Detect([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        IEnumerable<IntroCreditsResultDto> results = await _introCreditsService.DetectAsync(item, cancellationToken).ConfigureAwait(false);
        return Ok(results);
    }
}
