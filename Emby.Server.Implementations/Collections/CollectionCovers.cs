using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Collections;

/// <summary>
/// Gives collections without a primary image a cover: the backdrops of their first 2 or 3 movies (release order)
/// stacked in strips, saved as the real primary image, so every app shows it (cards and the collection page).
/// Collections without a backdrop also get the backdrop of their first movie.
/// </summary>
internal static class CollectionCovers
{
    private const int Width = 1000;
    private const int Height = 1500;

    /// <summary>
    /// New collections get some time for the metadata providers (TMDb poster) before a cover is made.
    /// </summary>
    private static readonly TimeSpan _graceTime = TimeSpan.FromHours(6);

    public static async Task<int> GenerateAsync(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IImageProcessor imageProcessor,
        IApplicationPaths appPaths,
        ILogger logger,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        if (!imageProcessor.SupportsImageCollageCreation)
        {
            progress.Report(100);
            return 0;
        }

        var now = DateTime.UtcNow;
        var boxSets = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Recursive = true
        })
            .OfType<BoxSet>()
            .Where(b => !b.HasImage(ImageType.Primary) && b.DateLastRefreshed != default && now - b.DateCreated > _graceTime)
            .ToList();

        var made = 0;
        for (var i = 0; i < boxSets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await MakeCoverAsync(boxSets[i], providerManager, imageProcessor, appPaths, cancellationToken).ConfigureAwait(false))
                {
                    made++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not make the cover of the collection {Name}", boxSets[i].Name);
            }

            progress.Report(100.0 * (i + 1) / boxSets.Count);
        }

        if (made > 0)
        {
            logger.LogInformation("Covers made for {Count} collections without a primary image", made);
        }

        progress.Report(100);
        return made;
    }

    private static async Task<bool> MakeCoverAsync(BoxSet boxSet, IProviderManager providerManager, IImageProcessor imageProcessor, IApplicationPaths appPaths, CancellationToken cancellationToken)
    {
        var children = boxSet.GetLinkedChildren()
            .Where(c => c is Video)
            .OrderBy(c => c.PremiereDate ?? (c.ProductionYear is int year && year > 0 ? new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc) : DateTime.MaxValue))
            .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var backdrops = LocalImages(children, ImageType.Backdrop);
        var paths = backdrops.Count > 0 ? backdrops : LocalImages(children, ImageType.Primary);
        if (paths.Count == 0)
        {
            return false;
        }

        Directory.CreateDirectory(appPaths.TempDirectory);
        var output = Path.Combine(appPaths.TempDirectory, "collection-cover-" + Guid.NewGuid().ToString("N") + ".jpg");
        imageProcessor.CreateImageCollage(
            new ImageCollageOptions
            {
                InputPaths = paths.Take(3).ToArray(),
                OutputPath = output,
                Width = Width,
                Height = Height,
                Strips = true
            },
            boxSet.Name);

        if (!File.Exists(output))
        {
            return false;
        }

        // SaveImage moves the temporary file into the collection's metadata folder
        await providerManager.SaveImage(boxSet, output, "image/jpeg", ImageType.Primary, null, null, cancellationToken).ConfigureAwait(false);

        // The collection page shows the backdrop at the top: the first movie's one when it has none
        if (!boxSet.HasImage(ImageType.Backdrop) && backdrops.Count > 0)
        {
            var copy = Path.Combine(appPaths.TempDirectory, "collection-backdrop-" + Guid.NewGuid().ToString("N") + Path.GetExtension(backdrops[0]));
            File.Copy(backdrops[0], copy);
            await providerManager.SaveImage(boxSet, copy, MimeTypes.GetMimeType(copy), ImageType.Backdrop, null, null, cancellationToken).ConfigureAwait(false);
        }

        await boxSet.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static List<string> LocalImages(IEnumerable<BaseItem> items, ImageType type)
        => items
            .Select(i => i.GetImageInfo(type, 0))
            .Where(img => img is not null && img.IsLocalFile && File.Exists(img.Path))
            .Select(img => img!.Path)
            .Take(3)
            .ToList();
}

/// <summary>
/// Scheduled task: covers for collections without a primary image.
/// </summary>
public class CollectionCoversTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<CollectionCoversTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionCoversTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="imageProcessor">The image processor.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public CollectionCoversTask(ILibraryManager libraryManager, IProviderManager providerManager, IImageProcessor imageProcessor, IApplicationPaths appPaths, ILogger<CollectionCoversTask> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _imageProcessor = imageProcessor;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Crear portadas de colecciones";

    /// <inheritdoc />
    public string Description => "A las colecciones sin imagen principal les crea una portada con los fondos de sus primeras películas (y les pone el fondo de la primera película si no tienen uno).";

    /// <inheritdoc />
    public string Category => "Biblioteca";

    /// <inheritdoc />
    public string Key => "CollectionCovers";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks
        };
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => CollectionCovers.GenerateAsync(_libraryManager, _providerManager, _imageProcessor, _appPaths, _logger, progress, cancellationToken);
}

/// <summary>
/// After every library scan: covers for the new collections without a primary image.
/// </summary>
public class CollectionCoversPostScanTask : ILibraryPostScanTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<CollectionCoversPostScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionCoversPostScanTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="imageProcessor">The image processor.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public CollectionCoversPostScanTask(ILibraryManager libraryManager, IProviderManager providerManager, IImageProcessor imageProcessor, IApplicationPaths appPaths, ILogger<CollectionCoversPostScanTask> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _imageProcessor = imageProcessor;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        => CollectionCovers.GenerateAsync(_libraryManager, _providerManager, _imageProcessor, _appPaths, _logger, progress, cancellationToken);
}
