using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// What each folder of a mixed library holds (movies or series). Jellyfin uses it when scanning: everything inside a
/// folder marked as series is read as series and episodes, everything inside one marked as movies as movies,
/// instead of guessing folder by folder (anime episodes were often taken as movies).
/// </summary>
[Route("Library/VirtualFolders/ContentTypes")]
[Authorize(Policy = Policies.RequiresElevation)]
public class LibraryContentTypesController : BaseJellyfinApiController
{
    private static readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase) { "movies", "tvshows" };

    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _config;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<LibraryContentTypesController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryContentTypesController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="config">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{LibraryContentTypesController}"/> interface.</param>
    public LibraryContentTypesController(ILibraryManager libraryManager, IServerConfigurationManager config, IFileSystem fileSystem, ILogger<LibraryContentTypesController> logger)
    {
        _libraryManager = libraryManager;
        _config = config;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Gets the content type of each folder of a library.
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <response code="200">Folders returned ("movies", "tvshows" or empty for automatic).</response>
    /// <response code="404">Library not found.</response>
    /// <returns>The folders and their type.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<FolderContentType>> Get([FromQuery, Required] string name)
    {
        var locations = GetLocations(name);
        if (locations is null)
        {
            return NotFound();
        }

        return locations
            .Select(path => new FolderContentType
            {
                Path = path,
                ContentType = _config.Configuration.ContentTypes.FirstOrDefault(c => !string.IsNullOrEmpty(c.Name) && _fileSystem.AreEqual(c.Name, path))?.Value
            })
            .ToList();
    }

    /// <summary>
    /// Sets the content type of folders of a library. Those folders are read again right away (unless refresh is
    /// false), so their items become movies or series without scanning every library.
    /// </summary>
    /// <param name="request">The library and its folders.</param>
    /// <param name="refresh">Whether to read the folders again.</param>
    /// <response code="204">Saved.</response>
    /// <response code="404">Library not found.</response>
    /// <returns>No content.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Set([FromBody, Required] FolderContentTypesRequest request, [FromQuery] bool refresh = true)
    {
        var locations = GetLocations(request.Name);
        if (locations is null)
        {
            return NotFound();
        }

        // Only folders of this library
        var changes = (request.Folders ?? [])
            .Where(f => !string.IsNullOrEmpty(f.Path) && locations.Any(l => _fileSystem.AreEqual(l, f.Path)))
            .ToList();

        var list = _config.Configuration.ContentTypes
            .Where(c => !changes.Any(f => !string.IsNullOrEmpty(c.Name) && _fileSystem.AreEqual(c.Name, f.Path)))
            .ToList();

        foreach (var folder in changes)
        {
            if (folder.ContentType is not null && _allowed.Contains(folder.ContentType))
            {
                list.Add(new NameValuePair(folder.Path, folder.ContentType.ToLowerInvariant()));
            }
        }

        _config.Configuration.ContentTypes = list.ToArray();
        _config.SaveConfiguration();

        if (refresh)
        {
            var paths = changes.Select(f => f.Path).ToList();
            _ = Task.Run(() => LibraryFolderReader.ReadAsync(_libraryManager, paths, _logger));
        }

        return NoContent();
    }

    private IReadOnlyList<string>? GetLocations(string? name)
        => _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Locations;

    /// <summary>
    /// A folder and what it holds.
    /// </summary>
    public class FolderContentType
    {
        /// <summary>Gets or sets the folder path.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Gets or sets the content type: "movies", "tvshows" or null for automatic.</summary>
        public string? ContentType { get; set; }
    }

    /// <summary>
    /// The folders of a library and what each one holds.
    /// </summary>
    public class FolderContentTypesRequest
    {
        /// <summary>Gets or sets the library name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the folders.</summary>
        public IReadOnlyList<FolderContentType>? Folders { get; set; }
    }
}
