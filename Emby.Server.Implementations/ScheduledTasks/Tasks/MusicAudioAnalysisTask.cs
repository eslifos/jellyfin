using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MusicHub;
using MediaBrowser.Model.Tasks;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Listens to the new songs when the server starts and every twelve hours, and updates the moods with the result.
/// </summary>
public class MusicAudioAnalysisTask : IScheduledTask
{
    private readonly IMusicAudioAnalyzer _audio;
    private readonly IMusicHubService _music;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicAudioAnalysisTask"/> class.
    /// </summary>
    /// <param name="audio">The audio analyzer.</param>
    /// <param name="music">The music section.</param>
    public MusicAudioAnalysisTask(IMusicAudioAnalyzer audio, IMusicHubService music)
    {
        _audio = audio;
        _music = music;
    }

    /// <inheritdoc />
    public string Name => "Escuchar y analizar canciones";

    /// <inheritdoc />
    public string Description => "Escucha cada canción para medir su ritmo, tonalidad, energía y bailabilidad, y mejora los estados de ánimo, las canciones parecidas y el DJ. Solo procesa canciones nuevas o cambiadas.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "MusicAudioAnalysis";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _audio.AnalyzeAsync(new Progress<double>(p => progress.Report(p * 0.95)), cancellationToken).ConfigureAwait(false);
        await _music.BuildAsync(new Progress<double>(p => progress.Report(95 + (p * 0.05))), cancellationToken).ConfigureAwait(false);
    }
}
