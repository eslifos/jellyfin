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
    /// Saves the display order and main source, then refreshes the series and its episodes right away (in the background).
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="displayOrder">The display order ("" = aired).</param>
    /// <param name="source">The main source, or <c>null</c> for the library order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>false</c> if the series is already being refreshed.</returns>
    Task<bool> ApplyAsync(Series series, string displayOrder, string? source, CancellationToken cancellationToken);

    /// <summary>
    /// Reads again the season / episode numbers of the files that have none.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many episodes were fixed and how many still have no number.</returns>
    Task<(int Fixed, int Remaining)> FixEpisodeNumbersAsync(Series series, CancellationToken cancellationToken);
}
