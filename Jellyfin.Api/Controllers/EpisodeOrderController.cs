using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.EpisodeOrder;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.EpisodeOrder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Episode orders and main metadata source of a series (dashboard).
/// </summary>
[Route("EpisodeOrder")]
[Authorize(Policy = Policies.RequiresElevation)]
public class EpisodeOrderController : BaseJellyfinApiController
{
    private readonly IEpisodeOrderService _episodeOrder;
    private readonly IMetadataSourcePreferences _sourcePreferences;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeOrderController"/> class.
    /// </summary>
    /// <param name="episodeOrder">The episode order service.</param>
    /// <param name="sourcePreferences">The per-series main source.</param>
    /// <param name="libraryManager">The library manager.</param>
    public EpisodeOrderController(IEpisodeOrderService episodeOrder, IMetadataSourcePreferences sourcePreferences, ILibraryManager libraryManager)
    {
        _episodeOrder = episodeOrder;
        _sourcePreferences = sourcePreferences;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Compares the series' episodes with the orders TVDB and TMDb have for it.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="refresh">Whether to read the providers again.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Analysis returned.</response>
    /// <response code="404">Series not found.</response>
    /// <returns>The analysis.</returns>
    [HttpGet("{seriesId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EpisodeOrderAnalysis>> Analyze([FromRoute] Guid seriesId, [FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (_libraryManager.GetItemById(seriesId) is not Series series)
        {
            return NotFound();
        }

        return Ok(await _episodeOrder.AnalyzeAsync(series, refresh, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Sets the main metadata source of a series (empty = library order). Refresh the series afterwards.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="source">The metadata fetcher name (e.g. "TheTVDB").</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Saved.</response>
    /// <response code="400">Unknown source.</response>
    /// <response code="404">Series not found.</response>
    /// <returns>No content.</returns>
    [HttpPost("{seriesId}/Source")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetSource([FromRoute] Guid seriesId, [FromQuery] string? source, CancellationToken cancellationToken = default)
    {
        if (_libraryManager.GetItemById(seriesId) is not Series series)
        {
            return NotFound();
        }

        if (!string.IsNullOrEmpty(source))
        {
            var analysis = await _episodeOrder.AnalyzeAsync(series, false, cancellationToken).ConfigureAwait(false);
            if (!analysis.Sources.Contains(source, StringComparer.OrdinalIgnoreCase))
            {
                return BadRequest();
            }
        }

        _sourcePreferences.SetPrimarySource(seriesId, source);
        return NoContent();
    }

    /// <summary>
    /// Reads again the season / episode numbers of files that have none.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Done.</response>
    /// <response code="404">Series not found.</response>
    /// <returns>Fixed and remaining counts.</returns>
    [HttpPost("{seriesId}/FixNumbers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> FixNumbers([FromRoute] Guid seriesId, CancellationToken cancellationToken = default)
    {
        if (_libraryManager.GetItemById(seriesId) is not Series series)
        {
            return NotFound();
        }

        var (fixedCount, remaining) = await _episodeOrder.FixEpisodeNumbersAsync(series, cancellationToken).ConfigureAwait(false);
        return Ok(new { Fixed = fixedCount, Remaining = remaining });
    }

    /// <summary>
    /// Gets every series with its own main source.
    /// </summary>
    /// <response code="200">Map returned.</response>
    /// <returns>Series id to source name.</returns>
    [HttpGet("Sources")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetSources()
        => Ok(_sourcePreferences.GetAll().ToDictionary(kv => kv.Key.ToString("N"), kv => kv.Value));
}
