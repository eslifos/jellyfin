using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Maps the genres already saved to the language of each library ("Comedy" and "Comedia" become "Comedia"), with the
/// same map used when an item is saved, and removes the genres left without items.
/// </summary>
public class UnifyGenresTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IGenreNormalizer _normalizer;
    private readonly ILogger<UnifyGenresTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnifyGenresTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="normalizer">The genre map.</param>
    /// <param name="logger">The logger.</param>
    public UnifyGenresTask(ILibraryManager libraryManager, IGenreNormalizer normalizer, ILogger<UnifyGenresTask> logger)
    {
        _libraryManager = libraryManager;
        _normalizer = normalizer;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Unificar géneros";

    /// <inheritdoc />
    public string Description => "Pasa al idioma de cada biblioteca los géneros que proveedores o archivos NFO guardaron en inglés (\"Comedy\" y \"Comedia\" quedan como \"Comedia\"), con el mapa de Bibliotecas → Metadatos, y quita los géneros que se quedan sin elementos.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "UnifyGenres";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // New items are mapped when saved; once a week catches anything saved some other way
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(7).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Only the items that still have a genre the map knows how to change
        var sources = _normalizer.GetSourceGenres().ToArray();
        var ids = sources.Length == 0 ? [] : _libraryManager.GetItemIds(new InternalItemsQuery
        {
            Genres = sources,
            Recursive = true,
            IsVirtualItem = false
        });

        var changed = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = _libraryManager.GetItemById(ids[i]);
            if (item is not null && _normalizer.Normalize(item))
            {
                try
                {
                    await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    changed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "The genres of {ItemName} could not be saved", item.Name);
                }
            }

            progress.Report(95.0 * (i + 1) / ids.Count);
        }

        // "Comedy" may now be a genre with nothing in it
        var dead = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Genre],
            IsDeadGenre = true,
            IsLocked = false
        });
        foreach (var genre in dead)
        {
            _logger.LogInformation("Deleting genre left without items {ItemId} {ItemName}", genre.Id.ToString("N", CultureInfo.InvariantCulture), genre.Name);
        }

        _libraryManager.DeleteItemsUnsafeFast(dead, deleteSourceFiles: true);
        _logger.LogInformation("Genres unified in {Changed} of {Checked} items; {Deleted} genres removed", changed, ids.Count, dead.Count);
        progress.Report(100);
    }
}
