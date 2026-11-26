using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Genres;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The map that keeps the genres of each library in its language ("Comedy" → "Comedia"). Administrators only.
/// </summary>
[Route("GenreMap")]
[Authorize(Policy = Policies.RequiresElevation)]
public class GenreMapController : BaseJellyfinApiController
{
    private readonly IGenreNormalizer _normalizer;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenreMapController"/> class.
    /// </summary>
    /// <param name="normalizer">The genre map.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="config">The server configuration.</param>
    public GenreMapController(IGenreNormalizer normalizer, ILibraryManager libraryManager, IServerConfigurationManager config)
    {
        _normalizer = normalizer;
        _libraryManager = libraryManager;
        _config = config;
    }

    /// <summary>
    /// Gets the map of every language, the ones the libraries use first.
    /// </summary>
    /// <response code="200">Map returned.</response>
    /// <returns>The map.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<GenreMap> GetMap()
    {
        var inUse = _libraryManager.GetVirtualFolders()
            .Select(f => f.LibraryOptions?.PreferredMetadataLanguage)
            .Append(_config.Configuration.PreferredMetadataLanguage)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!)
            .ToList();
        return _normalizer.GetMap(inUse);
    }

    /// <summary>
    /// Replaces the equivalences of one language. The task "Unificar géneros" applies them to what is saved.
    /// </summary>
    /// <param name="language">The language code, like "es".</param>
    /// <param name="body">The equivalences.</param>
    /// <response code="204">Map saved.</response>
    /// <response code="400">A language code or an equivalence is not valid.</response>
    /// <returns>The result.</returns>
    [HttpPost("{language}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult SetMap([FromRoute] string language, [FromBody] GenreMapUpdate body)
    {
        try
        {
            _normalizer.SetMap(language, body?.Entries ?? []);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        return NoContent();
    }

    /// <summary>
    /// Goes back to the built-in equivalences of one language.
    /// </summary>
    /// <param name="language">The language code.</param>
    /// <response code="204">Map reset.</response>
    /// <response code="400">The language code is not valid.</response>
    /// <returns>The result.</returns>
    [HttpDelete("{language}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult ResetMap([FromRoute] string language)
    {
        try
        {
            _normalizer.ResetMap(language);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        return NoContent();
    }
}
