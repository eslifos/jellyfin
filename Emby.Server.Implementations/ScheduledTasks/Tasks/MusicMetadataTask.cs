using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MusicHub;
using MediaBrowser.Model.Tasks;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Completes the albums and artists that are missing data when the server starts and once a day.
/// </summary>
public class MusicMetadataTask : IScheduledTask
{
    private readonly IMusicMetadataService _metadata;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicMetadataTask"/> class.
    /// </summary>
    /// <param name="metadata">The service that completes the music data.</param>
    public MusicMetadataTask(IMusicMetadataService metadata)
    {
        _metadata = metadata;
    }

    /// <inheritdoc />
    public string Name => "Completar metadatos de música";

    /// <inheritdoc />
    public string Description => "Busca en Deezer, iTunes, Wikipedia y Last.fm las portadas, años, géneros, fotos, biografías y descripciones que faltan. Nunca reemplaza lo que ya existe, envía solo nombres de artista y álbum, y espera si hay un escaneo de biblioteca en curso. Last.fm se usa solo si tiene clave.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "MusicMetadata";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        };
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _metadata.FillAsync(progress, cancellationToken);
}
