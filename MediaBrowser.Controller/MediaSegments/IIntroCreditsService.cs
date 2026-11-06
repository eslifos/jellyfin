using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaSegments;

namespace MediaBrowser.Controller.MediaSegments;

/// <summary>
/// On demand intro / credits detection for a single movie, episode, season or series.
/// </summary>
public interface IIntroCreditsService
{
    /// <summary>
    /// Gets the stored results of an item (every episode for a season or series).
    /// </summary>
    /// <param name="item">A movie, episode, season or series.</param>
    /// <returns>The results.</returns>
    IReadOnlyList<IntroCreditsResultDto> GetResults(BaseItem item);

    /// <summary>
    /// Analyzes an item right now, replacing any previous result, and saves the media segments.
    /// </summary>
    /// <param name="item">A movie, episode, season or series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new results.</returns>
    Task<IReadOnlyList<IntroCreditsResultDto>> DetectAsync(BaseItem item, CancellationToken cancellationToken);
}
