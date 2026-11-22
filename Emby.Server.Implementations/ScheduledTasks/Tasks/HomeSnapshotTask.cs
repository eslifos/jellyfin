using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Home;
using MediaBrowser.Model.Tasks;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Prepares the home page (banner, top 10, rows, genres) every 3 hours, so opening it costs almost nothing.
/// </summary>
public class HomeSnapshotTask : IScheduledTask
{
    private readonly IHomeSnapshotService _home;
    private readonly IAudiobookCatalog _audiobooks;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeSnapshotTask"/> class.
    /// </summary>
    /// <param name="home">The home page service.</param>
    /// <param name="audiobooks">The audiobooks of each library.</param>
    public HomeSnapshotTask(IHomeSnapshotService home, IAudiobookCatalog audiobooks)
    {
        _home = home;
        _audiobooks = audiobooks;
    }

    /// <inheritdoc />
    public string Name => "Preparar la página de Inicio";

    /// <inheritdoc />
    public string Description => "Arma de antemano el banner, los Top 10, lo nuevo, los estrenos, lo mejor valorado y los géneros del Inicio y de cada biblioteca, y los audiolibros, para que abra al instante sin cargar al servidor.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "HomeSnapshot";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _home.BuildAsync(new Progress<double>(p => progress.Report(p * 0.9)), cancellationToken).ConfigureAwait(false);
        await _audiobooks.BuildAllAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
