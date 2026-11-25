using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MusicHub;
using MediaBrowser.Model.Lyrics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The lyrics of the music section.
/// </summary>
[Route("MusicHub/Songs/{itemId}/Lyrics")]
[Authorize]
public class MusicLyricsController : BaseJellyfinApiController
{
    private readonly IMusicLyricsService _lyrics;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicLyricsController"/> class.
    /// </summary>
    /// <param name="lyrics">The lyrics of the music section.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    public MusicLyricsController(IMusicLyricsService lyrics, IUserManager userManager, ILibraryManager libraryManager)
    {
        _lyrics = lyrics;
        _userManager = userManager;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets the lyrics of a song, looking for them online if needed.
    /// </summary>
    /// <param name="itemId">The song.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Lyrics returned; without lines if there are none.</response>
    /// <response code="404">The song does not exist for this user.</response>
    /// <returns>The lyrics.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MusicSongLyrics>> GetLyrics([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var song = Song(itemId);
        if (song is null)
        {
            return NotFound();
        }

        return Answer(await _lyrics.GetAsync(song, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Lists other lyrics that could belong to a song.
    /// </summary>
    /// <param name="itemId">The song.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Options returned.</response>
    /// <response code="404">The song does not exist for this user.</response>
    /// <response code="503">The lyrics service did not answer.</response>
    /// <returns>The options.</returns>
    [HttpGet("Options")]
    [Authorize(Policy = Policies.LyricManagement)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<MusicLyricOption>>> GetOptions([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var song = Song(itemId);
        if (song is null)
        {
            return NotFound();
        }

        try
        {
            return Ok(await _lyrics.SearchAsync(song, cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Keeps one of the options as the lyrics of a song.
    /// </summary>
    /// <param name="itemId">The song.</param>
    /// <param name="optionId">The option.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Lyrics saved.</response>
    /// <response code="404">The song or the option does not exist.</response>
    /// <response code="503">The lyrics service did not answer.</response>
    /// <returns>The lyrics.</returns>
    [HttpPost("Options/{optionId}")]
    [Authorize(Policy = Policies.LyricManagement)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MusicSongLyrics>> Choose([FromRoute] Guid itemId, [FromRoute] long optionId, CancellationToken cancellationToken)
    {
        var song = Song(itemId);
        if (song is null)
        {
            return NotFound();
        }

        try
        {
            var lyrics = await _lyrics.ChooseAsync(song, optionId, cancellationToken).ConfigureAwait(false);
            return lyrics is null ? NotFound() : Answer(lyrics);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private Audio? Song(Guid itemId) => _libraryManager.GetItemById<Audio>(itemId, User.GetUserId());

    private MusicSongLyrics Answer(MusicLyrics lyrics)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        return new MusicSongLyrics(lyrics.Lines, lyrics.Instrumental, lyrics.FromLrclib, user?.HasPermission(PermissionKind.EnableLyricManagement) == true);
    }
}

/// <summary>
/// The lyrics of a song for the player.
/// </summary>
/// <param name="Lyrics">The lines.</param>
/// <param name="Instrumental">Whether the song has no singing.</param>
/// <param name="FromLrclib">Whether they were found on LRCLIB.</param>
/// <param name="CanChange">Whether this user can choose other lyrics.</param>
public sealed record MusicSongLyrics(IReadOnlyList<LyricLine> Lyrics, bool Instrumental, bool FromLrclib, bool CanChange);
