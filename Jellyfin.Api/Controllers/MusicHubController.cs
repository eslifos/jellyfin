using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MusicHub;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The music section: moods, similar songs, the analysis of a song and its video.
/// </summary>
[Route("MusicHub")]
[Authorize]
public class MusicHubController : BaseJellyfinApiController
{
    private const int MaxTracks = 100;

    private readonly IMusicHubService _music;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;
    private readonly IMediaEncoder _mediaEncoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicHubController"/> class.
    /// </summary>
    /// <param name="music">The music section.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="dtoService">The item writer.</param>
    /// <param name="mediaEncoder">The media encoder.</param>
    public MusicHubController(IMusicHubService music, IUserManager userManager, ILibraryManager libraryManager, IDtoService dtoService, IMediaEncoder mediaEncoder)
    {
        _music = music;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _dtoService = dtoService;
        _mediaEncoder = mediaEncoder;
    }

    /// <summary>
    /// Gets the moods that have songs.
    /// </summary>
    /// <response code="200">Moods returned.</response>
    /// <returns>The moods.</returns>
    [HttpGet("Moods")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MusicMood>> GetMoods() => Ok(_music.GetMoods());

    /// <summary>
    /// Gets songs of a mood.
    /// </summary>
    /// <param name="mood">The key of the mood.</param>
    /// <param name="parentId">The library.</param>
    /// <param name="limit">How many songs.</param>
    /// <response code="200">Songs returned.</response>
    /// <returns>The songs.</returns>
    [HttpGet("Moods/{mood}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<IReadOnlyList<BaseItemDto>> GetMoodTracks([FromRoute] string mood, [FromQuery] Guid? parentId, [FromQuery] int limit = 50)
    {
        var user = CurrentUser();
        return user is null ? Unauthorized() : Ok(Write(_music.GetMoodTracks(user, mood, parentId, Math.Clamp(limit, 1, MaxTracks)), user));
    }

    /// <summary>
    /// Gets the songs most similar to one.
    /// </summary>
    /// <param name="itemId">The song.</param>
    /// <param name="limit">How many songs.</param>
    /// <response code="200">Songs returned.</response>
    /// <response code="404">The song does not exist for this user.</response>
    /// <returns>The songs.</returns>
    [HttpGet("Similar/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<BaseItemDto>> GetSimilar([FromRoute] Guid itemId, [FromQuery] int limit = 30)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        return Song(itemId, user) is null ? NotFound() : Ok(Write(_music.GetSimilar(user, itemId, Math.Clamp(limit, 1, MaxTracks)), user));
    }

    /// <summary>
    /// Gets the analysis of a song and whether it has a video.
    /// </summary>
    /// <param name="itemId">The song.</param>
    /// <response code="200">Information returned.</response>
    /// <response code="404">The song does not exist for this user.</response>
    /// <returns>The information.</returns>
    [HttpGet("Songs/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<MusicSongInfo> GetSong([FromRoute] Guid itemId)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var song = Song(itemId, user);
        return song is null ? NotFound() : new MusicSongInfo(_music.FindVideo(song) is not null, _music.GetAnalysis(itemId));
    }

    /// <summary>
    /// Streams the video that goes with a song.
    /// </summary>
    /// <param name="itemId">The song.</param>
    /// <response code="200">Video returned.</response>
    /// <response code="403">The user cannot play media.</response>
    /// <response code="404">The song has no video.</response>
    /// <returns>The video.</returns>
    [HttpGet("Songs/{itemId}/Video")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetVideo([FromRoute] Guid itemId)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        if (!user.HasPermission(PermissionKind.EnableMediaPlayback))
        {
            return Forbid();
        }

        var song = Song(itemId, user);
        var path = song is null ? null : _music.FindVideo(song);
        if (path is null || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        if (Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            await StreamAsMp4Async(path).ConfigureAwait(false);
            return new EmptyResult();
        }

        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            _ => "video/mp4"
        };
        return PhysicalFile(path, type, true);
    }

    /// <summary>Browsers do not open .mkv: the same picture goes in an MP4 as it is sent, and only the sound is converted.</summary>
    private async Task StreamAsMp4Async(string path)
    {
        var start = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        string[] arguments =
        [
            "-nostdin", "-hide_banner", "-loglevel", "fatal", "-i", path, "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-c:v", "copy",
            "-c:a", "aac", "-b:a", "192k", "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", "pipe:1"
        ];
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var ffmpeg = Process.Start(start) ?? throw new InvalidOperationException("The media encoder could not start");
        Response.ContentType = "video/mp4";
        try
        {
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(Response.Body, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The viewer left: the conversion ends below
        }
        finally
        {
            if (!ffmpeg.HasExited)
            {
                ffmpeg.Kill(true);
            }
        }
    }

    private User? CurrentUser() => _userManager.GetUserById(User.GetUserId());

    private Audio? Song(Guid itemId, User user) => _libraryManager.GetItemById<Audio>(itemId, user);

    private IReadOnlyList<BaseItemDto> Write(IReadOnlyList<BaseItem> items, User user)
        => _dtoService.GetBaseItemDtos(items, new DtoOptions(false) { EnableImages = true }, user);
}

/// <summary>
/// What the player needs to know of a song.
/// </summary>
/// <param name="HasVideo">Whether it has a video.</param>
/// <param name="Analysis">Its analysis, if it was analyzed.</param>
public sealed record MusicSongInfo(bool HasVideo, MusicAnalysis? Analysis);
