using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Reads library folders right after they are added or their content type changes, without a full scan of
/// every library: the folder is registered in its library and everything in it is read on the refresh queue.
/// If the server stops before it finishes, the "Buscar archivos nuevos" task reads it again.
/// </summary>
internal static class LibraryFolderReader
{
    /// <summary>
    /// Registers the folders in their libraries and reads all of their content.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="paths">The folders.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>A task.</returns>
    public static async Task ReadAsync(ILibraryManager libraryManager, IReadOnlyCollection<string> paths, ILogger logger)
    {
        if (paths.Count == 0)
        {
            return;
        }

        try
        {
            await libraryManager.ValidateTopLibraryFolders(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error registering the library folders");
        }

        foreach (var path in paths)
        {
            if (libraryManager.FindByPath(path, true) is Folder folder)
            {
                logger.LogInformation("Reading library folder {Path}", path);
                folder.ChangedExternally();
            }
            else
            {
                logger.LogWarning("Library folder {Path} is not reachable, it will be read by the next new files check", path);
            }
        }
    }
}
