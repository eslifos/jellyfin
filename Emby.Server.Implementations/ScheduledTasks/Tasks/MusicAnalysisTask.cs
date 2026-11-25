using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MusicHub;
using MediaBrowser.Model.Tasks;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Classifies the songs by energy and mood when the server starts and every six hours.
/// </summary>
public class MusicAnalysisTask : IScheduledTask
{
    private readonly IMusicHubService _music;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicAnalysisTask"/> class.
    /// </summary>
    /// <param name="music">The music section.</param>
    public MusicAnalysisTask(IMusicHubService music)
    {
        _music = music;
    }

    /// <inheritdoc />
    public string Name => "Analizar la música";

    /// <inheritdoc />
    public string Description => "Clasifica las canciones por energía y estado de ánimo, y encuentra las que se parecen, a partir de sus géneros, títulos, artistas y año.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "MusicAnalysis";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _music.BuildAsync(progress, cancellationToken);
}
