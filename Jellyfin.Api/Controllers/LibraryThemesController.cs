using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Themes of the libraries (anime, kids), used by the app for the start screen, icons and colours.
/// </summary>
[Route("Library/ContentThemes")]
[Authorize]
public class LibraryThemesController : BaseJellyfinApiController
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryThemesController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    public LibraryThemesController(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets the theme of every library that has one.
    /// </summary>
    /// <response code="200">Themes returned: library id (without dashes) to "anime" or "kids".</response>
    /// <returns>The themes.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<Dictionary<string, string>> GetThemes()
    {
        var themes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            var theme = folder.LibraryOptions?.ContentTheme;
            if (!string.IsNullOrEmpty(theme) && Guid.TryParse(folder.ItemId, out var id))
            {
                themes[id.ToString("N", CultureInfo.InvariantCulture)] = theme;
            }
        }

        return themes;
    }
}
