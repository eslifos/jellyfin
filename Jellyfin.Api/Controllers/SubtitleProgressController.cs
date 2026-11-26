using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Subtitles while they are still being extracted from the video file. The first time, the server has to read the
/// whole file to get them (minutes on remote storage); with this the player shows the part already extracted,
/// which grows faster than the video plays, instead of waiting for the end.
/// </summary>
[Route("Videos")]
[Authorize]
public class SubtitleProgressController : BaseJellyfinApiController
{
    /// <summary>Header that says the extraction has finished (the regular subtitle request returns everything).</summary>
    public const string CompleteHeader = "X-Subtitles-Complete";

    /// <summary>Header with where the extraction stands: none, extracting, complete or failed.</summary>
    public const string StateHeader = "X-Subtitles-State";

    private readonly ILibraryManager _libraryManager;
    private readonly ISubtitleEncoder _subtitleEncoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleProgressController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="subtitleEncoder">Instance of the <see cref="ISubtitleEncoder"/> interface.</param>
    public SubtitleProgressController(ILibraryManager libraryManager, ISubtitleEncoder subtitleEncoder)
    {
        _libraryManager = libraryManager;
        _subtitleEncoder = subtitleEncoder;
    }

    /// <summary>
    /// Gets the part of a subtitle already extracted, as track events.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="mediaSourceId">The media source id.</param>
    /// <param name="index">The subtitle stream index.</param>
    /// <param name="retry">Try again an extraction that failed.</param>
    /// <param name="positionTicks">Where the video is (after a jump ahead, the subtitle is also read from there).</param>
    /// <response code="200">The subtitle so far.</response>
    /// <response code="204">Nothing to show yet (or nothing to extract).</response>
    /// <response code="404">Item not found.</response>
    /// <returns>The subtitle so far, with headers saying where the extraction stands. It never waits for it.</returns>
    /// <remarks>
    /// No file extension in the route: reverse proxies that cache static assets by extension (".js" among them,
    /// ignoring Cache-Control) would otherwise keep answering an old, unfinished subtitle.
    /// </remarks>
    [HttpGet("{itemId}/{mediaSourceId}/Subtitles/{index}/Partial")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetPartialSubtitles(
        [FromRoute, Required] Guid itemId,
        [FromRoute, Required] string mediaSourceId,
        [FromRoute, Required] int index,
        [FromQuery] bool retry = false,
        [FromQuery] long? positionTicks = null)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var partial = await _subtitleEncoder
            .GetPartialSubtitles(item, mediaSourceId, index, "json", retry, positionTicks, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        // "Complete" also when there is nothing to extract: the regular request answers at once
        Response.Headers[CompleteHeader] = partial.State is SubtitleExtractionState.Complete or SubtitleExtractionState.NotNeeded ? "true" : "false";
        Response.Headers[StateHeader] = partial.State switch
        {
            SubtitleExtractionState.Extracting => "extracting",
            SubtitleExtractionState.Complete => "complete",
            SubtitleExtractionState.Failed => "failed",
            _ => "none"
        };
        Response.Headers.CacheControl = "no-store";
        if (partial.Content is null)
        {
            return NoContent();
        }

        return File(partial.Content, "application/json");
    }
}
