using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Attributes;
using Jellyfin.Api.Extensions;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
    private readonly ILibraryManager _libraryManager;
    private readonly ITrailerVideoService _trailers;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoveryController"/> class.
    /// </summary>
    /// <param name="discovery">The discovery service.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="trailers">The trailer video service.</param>
    public DiscoveryController(IDiscoveryService discovery, IUserManager userManager, ILibraryManager libraryManager, ITrailerVideoService trailers)
    {
        _trailers = trailers;
        _discovery = discovery;
        _userManager = userManager;
        _libraryManager = libraryManager;
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
    /// Gets the rows of the start screen of a movie library.
    /// </summary>
    /// <param name="parentId">The library id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Rows returned.</response>
    /// <returns>The rows.</returns>
    [HttpGet("Library/{parentId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<LibraryDiscovery>> GetLibrary([FromRoute] Guid parentId, CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        return Ok(await _discovery.GetLibraryAsync(user, parentId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Gets the best trailer (YouTube) in the language of the library: es-MX / es-419, other Spanish, English, then the original language.
    /// </summary>
    /// <param name="itemId">A library item (movie or series); its TMDb id and library language are used.</param>
    /// <param name="tmdbId">Or a TMDb movie id (with <paramref name="parentId"/> for the language).</param>
    /// <param name="parentId">The library whose language to use with <paramref name="tmdbId"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Trailer returned.</response>
    /// <response code="204">No trailer found.</response>
    /// <returns>The trailer.</returns>
    [HttpGet("Trailer")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<TrailerInfo>> GetTrailer([FromQuery] Guid? itemId, [FromQuery] int? tmdbId, [FromQuery] Guid? parentId, CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        TrailerInfo? trailer = null;
        if (itemId is Guid id)
        {
            var item = _libraryManager.GetItemById<BaseItem>(id, user);
            if (item is null)
            {
                return NotFound();
            }

            var series = item is Episode ep ? ep.Series : item is Season se ? se.Series : item as Series;
            var source = (BaseItem?)series ?? item;
            if (int.TryParse(source.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb), NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemTmdb))
            {
                trailer = await _discovery.GetTrailerAsync(itemTmdb, series is not null, source.GetPreferredMetadataLanguage(), source.GetPreferredMetadataCountryCode(), source.OriginalLanguage, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (tmdbId is int tmdb)
        {
            var library = parentId is Guid p ? _libraryManager.GetItemById<BaseItem>(p, user) : null;
            trailer = await _discovery.GetTrailerAsync(tmdb, false, library?.GetPreferredMetadataLanguage(), library?.GetPreferredMetadataCountryCode(), null, cancellationToken).ConfigureAwait(false);
        }

        if (trailer is null)
        {
            return NoContent();
        }

        // Start downloading it now, so it is ready (or nearly) when the viewer presses play
        _ = _trailers.GetVideoAsync(trailer.Key, CancellationToken.None);
        return Ok(trailer);
    }

    /// <summary>
    /// Finds the best trailer that can be downloaded (library language first) and waits until it is ready to play.
    /// If one cannot be downloaded the next one is tried (other Spanish, English, original language).
    /// </summary>
    /// <param name="itemId">A library item (movie or series).</param>
    /// <param name="tmdbId">Or a TMDb movie id.</param>
    /// <param name="parentId">The library whose language to use with <paramref name="tmdbId"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The trailer, ready to play.</response>
    /// <response code="404">No trailer could be downloaded.</response>
    /// <returns>The trailer.</returns>
    [HttpGet("Trailer/Ready")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TrailerInfo>> GetReadyTrailer([FromQuery] Guid? itemId, [FromQuery] int? tmdbId, [FromQuery] Guid? parentId, CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        IReadOnlyList<TrailerInfo> candidates = [];
        if (itemId is Guid id)
        {
            var item = _libraryManager.GetItemById<BaseItem>(id, user);
            if (item is null)
            {
                return NotFound();
            }

            var series = item is Episode ep ? ep.Series : item is Season se ? se.Series : item as Series;
            var source = (BaseItem?)series ?? item;
            if (int.TryParse(source.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb), NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemTmdb))
            {
                candidates = await _discovery.GetTrailerCandidatesAsync(itemTmdb, series is not null, source.GetPreferredMetadataLanguage(), source.GetPreferredMetadataCountryCode(), source.OriginalLanguage, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (tmdbId is int tmdb)
        {
            var library = parentId is Guid p ? _libraryManager.GetItemById<BaseItem>(p, user) : null;
            candidates = await _discovery.GetTrailerCandidatesAsync(tmdb, false, library?.GetPreferredMetadataLanguage(), library?.GetPreferredMetadataCountryCode(), null, cancellationToken).ConfigureAwait(false);
        }

        foreach (var candidate in candidates)
        {
            if (await _trailers.GetVideoAsync(candidate.Key, cancellationToken).ConfigureAwait(false) is not null)
            {
                return Ok(candidate);
            }
        }

        return NotFound();
    }

    /// <summary>
    /// Waits until a trailer is downloaded to the server, so it can be played with the app player.
    /// </summary>
    /// <param name="key">The video key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Ready.</response>
    /// <response code="404">It could not be downloaded.</response>
    /// <returns>No content.</returns>
    [HttpGet("Trailer/{key}/Prepare")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> PrepareTrailer([FromRoute] string key, CancellationToken cancellationToken = default)
    {
        var file = await _trailers.GetVideoAsync(key, cancellationToken).ConfigureAwait(false);
        return file is null ? NotFound() : NoContent();
    }

    /// <summary>
    /// Gets the MP4 file of a downloaded trailer (supports seeking).
    /// </summary>
    /// <param name="key">The video key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The video.</response>
    /// <response code="404">It could not be downloaded.</response>
    /// <returns>The video file.</returns>
    [HttpGet("Trailer/{key}/Video")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesFile("video/mp4")]
    public async Task<ActionResult> GetTrailerVideo([FromRoute] string key, CancellationToken cancellationToken = default)
    {
        var file = _trailers.GetCachedVideo(key) ?? await _trailers.GetVideoAsync(key, cancellationToken).ConfigureAwait(false);
        return file is null ? NotFound() : PhysicalFile(file, "video/mp4", true);
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
