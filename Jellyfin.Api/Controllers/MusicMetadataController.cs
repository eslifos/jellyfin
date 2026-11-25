using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.MusicHub;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The Last.fm key used to complete the music data. Only administrators can use it and the key is never sent back.
/// </summary>
[Route("MusicHub/Metadata/LastFm")]
[Authorize(Policy = Policies.RequiresElevation)]
public class MusicMetadataController : BaseJellyfinApiController
{
    private readonly IMusicMetadataService _metadata;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicMetadataController"/> class.
    /// </summary>
    /// <param name="metadata">The service that completes the music data.</param>
    public MusicMetadataController(IMusicMetadataService metadata)
    {
        _metadata = metadata;
    }

    /// <summary>
    /// Says whether a key is saved.
    /// </summary>
    /// <response code="200">State returned.</response>
    /// <returns>The state.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MusicLastFmState> GetState() => new MusicLastFmState(_metadata.HasLastFmKey);

    /// <summary>
    /// Checks a key with Last.fm and saves it.
    /// </summary>
    /// <param name="body">The key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Key saved.</response>
    /// <response code="400">Last.fm does not accept the key.</response>
    /// <response code="502">Last.fm could not be reached.</response>
    /// <returns>The result.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> SetKey([FromBody] MusicLastFmKey body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body?.Key))
        {
            return BadRequest();
        }

        try
        {
            var saved = await _metadata.SetLastFmKeyAsync(body.Key, cancellationToken).ConfigureAwait(false);
            return saved ? NoContent() : BadRequest();
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Forgets the key.
    /// </summary>
    /// <response code="204">Key removed.</response>
    /// <returns>The result.</returns>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult RemoveKey()
    {
        _metadata.RemoveLastFmKey();
        return NoContent();
    }
}

/// <summary>
/// Whether a Last.fm key is saved.
/// </summary>
/// <param name="HasKey">Whether there is a key.</param>
public sealed record MusicLastFmState(bool HasKey);

/// <summary>
/// A Last.fm key.
/// </summary>
/// <param name="Key">The key.</param>
public sealed record MusicLastFmKey(string? Key);
