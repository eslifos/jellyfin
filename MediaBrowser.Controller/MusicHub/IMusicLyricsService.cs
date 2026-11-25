using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Lyrics;

namespace MediaBrowser.Controller.MusicHub;

/// <summary>
/// The lyrics of the music section: the ones the library has and, for the rest, the ones found online.
/// </summary>
public interface IMusicLyricsService
{
    /// <summary>
    /// Gets the lyrics of a song, looking for them online if nobody has done it yet.
    /// </summary>
    /// <param name="song">The song.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lyrics; without lines if there are none.</returns>
    Task<MusicLyrics> GetAsync(Audio song, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the other lyrics that could belong to a song.
    /// </summary>
    /// <param name="song">The song.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The options, the most likely first.</returns>
    Task<IReadOnlyList<MusicLyricOption>> SearchAsync(Audio song, CancellationToken cancellationToken);

    /// <summary>
    /// Keeps one of the options as the lyrics of a song.
    /// </summary>
    /// <param name="song">The song.</param>
    /// <param name="optionId">The option.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lyrics, or null if the option has none.</returns>
    Task<MusicLyrics?> ChooseAsync(Audio song, long optionId, CancellationToken cancellationToken);

    /// <summary>
    /// Looks for the lyrics of the songs that have none, little by little.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task FillAsync(IProgress<double> progress, CancellationToken cancellationToken);
}

/// <summary>
/// The lyrics of a song.
/// </summary>
/// <param name="Lines">The lines; empty if there are none.</param>
/// <param name="Instrumental">Whether the song has no singing.</param>
/// <param name="FromLrclib">Whether they were found on LRCLIB.</param>
public sealed record MusicLyrics(IReadOnlyList<LyricLine> Lines, bool Instrumental, bool FromLrclib);

/// <summary>
/// Lyrics that could belong to a song.
/// </summary>
/// <param name="Id">The id of the option.</param>
/// <param name="Title">The title.</param>
/// <param name="Artist">The artist.</param>
/// <param name="Album">The album.</param>
/// <param name="Seconds">The length of the song.</param>
/// <param name="Synced">Whether the lines follow the song.</param>
/// <param name="Preview">The first line.</param>
public sealed record MusicLyricOption(long Id, string Title, string Artist, string? Album, int? Seconds, bool Synced, string Preview);
