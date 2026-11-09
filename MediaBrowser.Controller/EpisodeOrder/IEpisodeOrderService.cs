using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.EpisodeOrder;

namespace MediaBrowser.Controller.EpisodeOrder;

/// <summary>
/// Compares a series' episode files with the episode orders TVDB and TMDb have for it.
/// </summary>
public interface IEpisodeOrderService
{
    /// <summary>
    /// Analyzes a series.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="refresh">Whether to ignore the cached provider data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The analysis.</returns>
    Task<EpisodeOrderAnalysis> AnalyzeAsync(Series series, bool refresh, CancellationToken cancellationToken);

    /// <summary>
    /// Reads again the season / episode numbers of the files that have none.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many episodes were fixed and how many still have no number.</returns>
    Task<(int Fixed, int Remaining)> FixEpisodeNumbersAsync(Series series, CancellationToken cancellationToken);
}
