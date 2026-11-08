using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Finds and repairs VobSub (DVD) subtitle tracks that declare an invalid 0x0 size,
/// which makes ffprobe fail to read the whole file.
/// </summary>
public interface IVobSubRepairService
{
    /// <summary>
    /// Gets the mkvmerge version, or null when mkvtoolnix is not installed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The version text or null.</returns>
    Task<string?> GetToolsVersionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Checks the VobSub tracks of an item without changing anything.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The inspection result.</returns>
    Task<VobSubRepairResult> InspectAsync(BaseItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Repairs the broken VobSub tracks of an item and queues a metadata refresh.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="keepBackup">Keep the original file as <c>.bak</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The repair result.</returns>
    Task<VobSubRepairResult> RepairAsync(BaseItem item, bool keepBackup, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the .bak copies left next to library video files.
    /// Only files named exactly "&lt;video path&gt;.bak" whose video still exists are returned.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The backups.</returns>
    IReadOnlyList<RepairBackupInfo> GetBackups(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the .bak copies of the given items, or all of them when no id is given.
    /// </summary>
    /// <param name="itemIds">The item ids, or null / empty for every backup found.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deleted backups.</returns>
    IReadOnlyList<RepairBackupInfo> DeleteBackups(IReadOnlyCollection<Guid>? itemIds, CancellationToken cancellationToken);
}
