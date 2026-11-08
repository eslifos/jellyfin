using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Tools to find and fix damaged media files.
/// </summary>
[Route("LibraryHealth")]
[Authorize(Policy = Policies.RequiresElevation)]
public class LibraryHealthController : BaseJellyfinApiController
{
    private readonly ILibraryManager _libraryManager;
    private readonly IVobSubRepairService _vobSubRepairService;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryHealthController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="vobSubRepairService">Instance of the <see cref="IVobSubRepairService"/> interface.</param>
    public LibraryHealthController(ILibraryManager libraryManager, IVobSubRepairService vobSubRepairService)
    {
        _libraryManager = libraryManager;
        _vobSubRepairService = vobSubRepairService;
    }

    /// <summary>
    /// Gets the mkvtoolnix version installed on the server.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Version returned; empty when mkvtoolnix is missing.</response>
    /// <returns>The version text.</returns>
    [HttpGet("VobSub/Tools")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<string>> GetTools(CancellationToken cancellationToken)
        => Ok(await _vobSubRepairService.GetToolsVersionAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty);

    /// <summary>
    /// Checks the VobSub subtitle tracks of an item without changing the file.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Inspection returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>The inspection result.</returns>
    [HttpPost("VobSub/{itemId}/Inspect")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VobSubRepairResult>> Inspect([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        return Ok(await _vobSubRepairService.InspectAsync(item, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Repairs the broken VobSub subtitle tracks of an item and refreshes it.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="keepBackup">Keep the original file as .bak.</param>
    /// <response code="200">Repair finished (see the status).</response>
    /// <response code="404">Item not found.</response>
    /// <returns>The repair result.</returns>
    [HttpPost("VobSub/{itemId}/Repair")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VobSubRepairResult>> Repair([FromRoute] Guid itemId, [FromQuery] bool keepBackup = true)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        // Do not abort halfway because the browser closed: the repair is safe to finish.
        return Ok(await _vobSubRepairService.RepairAsync(item, keepBackup, CancellationToken.None).ConfigureAwait(false));
    }

    /// <summary>
    /// Lists the .bak copies left next to repaired files.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Backups returned.</response>
    /// <returns>The backups.</returns>
    [HttpGet("Backups")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<RepairBackupInfo>> GetBackups(CancellationToken cancellationToken)
        => Ok(_vobSubRepairService.GetBackups(cancellationToken));

    /// <summary>
    /// Deletes the .bak copies of the given items, or every one found when no id is sent.
    /// </summary>
    /// <param name="itemIds">The item ids (optional).</param>
    /// <response code="200">The deleted backups.</response>
    /// <returns>The deleted backups.</returns>
    [HttpPost("Backups/Delete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<RepairBackupInfo>> DeleteBackups([FromBody] Guid[]? itemIds)
        => Ok(_vobSubRepairService.DeleteBackups(itemIds, CancellationToken.None));
}
