using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace MediaBrowser.Controller.MusicHub;

/// <summary>
/// The music section: the analysis of the songs (energy, mood, similar songs) and the videos that go with them.
/// </summary>
public interface IMusicHubService
{
    /// <summary>
    /// Analyzes the songs of the libraries and keeps the result on disk.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task BuildAsync(IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the moods that have enough songs.
    /// </summary>
    /// <returns>The moods.</returns>
    IReadOnlyList<MusicMood> GetMoods();

    /// <summary>
    /// Gets songs of a mood that the user can see.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="mood">The key of the mood.</param>
    /// <param name="parentId">The library, if only one.</param>
    /// <param name="limit">How many songs.</param>
    /// <returns>The songs, mixed.</returns>
    IReadOnlyList<BaseItem> GetMoodTracks(User user, string mood, Guid? parentId, int limit);

    /// <summary>
    /// Gets the songs most similar to one that the user can see.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="itemId">The song.</param>
    /// <param name="limit">How many songs.</param>
    /// <returns>The songs, most similar first.</returns>
    IReadOnlyList<BaseItem> GetSimilar(User user, Guid itemId, int limit);

    /// <summary>
    /// Gets the analysis of a song.
    /// </summary>
    /// <param name="itemId">The song.</param>
    /// <returns>The analysis, or null if it was not analyzed yet.</returns>
    MusicAnalysis? GetAnalysis(Guid itemId);

    /// <summary>
    /// Finds the video file that goes with a song, in the same folder.
    /// </summary>
    /// <param name="song">The song.</param>
    /// <returns>The path of the video, or null.</returns>
    string? FindVideo(Audio song);
}

/// <summary>
/// A mood with songs.
/// </summary>
/// <param name="Key">The key of the mood.</param>
/// <param name="Count">How many songs fit it.</param>
public sealed record MusicMood(string Key, int Count);

/// <summary>
/// What the analyzer knows of a song.
/// </summary>
/// <param name="Energy">From 0 (calm) to 1 (intense).</param>
/// <param name="Positivity">From 0 (sad) to 1 (happy).</param>
/// <param name="Mood">The key of its closest mood.</param>
/// <param name="Genres">Its genres.</param>
/// <param name="Year">Its year.</param>
/// <param name="Audio">What was measured in its sound, if it was listened to.</param>
public sealed record MusicAnalysis(double Energy, double Positivity, string Mood, IReadOnlyList<string> Genres, int? Year, MusicAudioFeatures? Audio = null);
