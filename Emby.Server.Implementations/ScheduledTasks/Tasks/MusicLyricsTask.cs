using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MusicHub;
using MediaBrowser.Model.Tasks;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Looks for the lyrics of the songs that have none when the server starts and once a day.
/// </summary>
public class MusicLyricsTask : IScheduledTask
{
    private readonly IMusicLyricsService _lyrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicLyricsTask"/> class.
    /// </summary>
    /// <param name="lyrics">The lyrics of the music section.</param>
    public MusicLyricsTask(IMusicLyricsService lyrics)
    {
        _lyrics = lyrics;
    }

    /// <inheritdoc />
    public string Name => "Buscar letras de canciones";

    /// <inheritdoc />
    public string Description => "Busca en LRCLIB las letras que faltan y las guarda en el servidor. Envía solo título, artista, álbum y duración, y no toca los archivos de música.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "MusicLyrics";

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
        => _lyrics.FillAsync(progress, cancellationToken);
}
