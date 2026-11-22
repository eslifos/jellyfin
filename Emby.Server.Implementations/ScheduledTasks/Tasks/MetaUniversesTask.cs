using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MetaUniverses;
using MediaBrowser.Model.Tasks;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Prepares the universes when the server starts and every hour (only if the libraries changed), so they open at once.
/// </summary>
public class MetaUniversesTask : IScheduledTask
{
    private readonly IMetaUniverseService _universes;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetaUniversesTask"/> class.
    /// </summary>
    /// <param name="universes">The universes.</param>
    public MetaUniversesTask(IMetaUniverseService universes)
    {
        _universes = universes;
    }

    /// <inheritdoc />
    public string Name => "Preparar los Universos";

    /// <inheritdoc />
    public string Description => "Arma de antemano los universos (películas, series, bandas sonoras, colecciones y actores) para que abran al instante. Cada hora revisa si cambiaron las bibliotecas y solo entonces los vuelve a armar.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "MetaUniverses";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(1).Ticks
        };
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _universes.BuildAsync(false, progress, cancellationToken);
}
