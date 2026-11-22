using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Model.Downloads;

namespace MediaBrowser.Controller.Downloads;

/// <summary>
/// Keeps what the devices report about their downloads, for the dashboard (in memory: the last 24 hours).
/// </summary>
public interface IDownloadMonitor
{
    /// <summary>
    /// Saves the progress of a download; a finished one is also written to the activity log.
    /// </summary>
    /// <param name="info">The download.</param>
    /// <returns>A task.</returns>
    Task ReportAsync(DownloadProgressInfo info);

    /// <summary>
    /// Gets the downloads: the active ones first, then the finished or failed ones of the last 24 hours.
    /// </summary>
    /// <returns>The downloads.</returns>
    IReadOnlyList<DownloadProgressInfo> GetAll();
}
