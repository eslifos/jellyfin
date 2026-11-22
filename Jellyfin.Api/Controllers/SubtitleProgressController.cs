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
    /// <response code="200">The subtitle so far.</response>
    /// <response code="204">Nothing extracted yet.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>The subtitle so far, with a header saying whether the extraction is complete.</returns>
    [HttpGet("{itemId}/{mediaSourceId}/Subtitles/{index}/Partial.js")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetPartialSubtitles(
        [FromRoute, Required] Guid itemId,
        [FromRoute, Required] string mediaSourceId,
        [FromRoute, Required] int index)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var (content, complete) = await _subtitleEncoder
            .GetPartialSubtitles(item, mediaSourceId, index, "json", HttpContext.RequestAborted)
            .ConfigureAwait(false);

        Response.Headers[CompleteHeader] = complete ? "true" : "false";
        Response.Headers.CacheControl = "no-store";
        if (content is null)
        {
            return NoContent();
        }

        return File(content, "application/json");
    }
}
