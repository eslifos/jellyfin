using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Imdb;
using MediaBrowser.Model.Tasks;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Downloads the IMDb datasets every day: ratings and votes for every title, and the IMDb ids of the episodes
/// of the library's series.
/// </summary>
public class ImdbRatingsTask : IScheduledTask
{
    private readonly IImdbRatingsService _imdb;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImdbRatingsTask"/> class.
    /// </summary>
    /// <param name="imdb">The IMDb ratings service.</param>
    public ImdbRatingsTask(IImdbRatingsService imdb)
    {
        _imdb = imdb;
    }

    /// <inheritdoc />
    public string Name => "Actualizar calificaciones de IMDb";

    /// <inheritdoc />
    public string Description => "Descarga los datos oficiales de IMDb (calificación y votos de cada título) y agrega el id de IMDb a los episodios que no lo tienen.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "ImdbRatings";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        };
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _imdb.UpdateAsync(progress, cancellationToken);
}
