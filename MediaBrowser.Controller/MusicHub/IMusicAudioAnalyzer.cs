using System;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.MusicHub;

/// <summary>
/// Listens to the songs and measures their rhythm, key and sound.
/// </summary>
public interface IMusicAudioAnalyzer
{
    /// <summary>
    /// Gets what was measured in a song.
    /// </summary>
    /// <param name="itemId">The song.</param>
    /// <returns>The measures, or null if it was not listened to yet.</returns>
    MusicAudioFeatures? Get(Guid itemId);

    /// <summary>
    /// Listens to the songs that are new or changed.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task AnalyzeAsync(IProgress<double> progress, CancellationToken cancellationToken);
}

/// <summary>
/// What was measured in the audio of a song.
/// </summary>
/// <param name="Bpm">The beats per minute, if the song has a clear beat.</param>
/// <param name="KeyRoot">The note of its key, from 0 (C) to 11 (B).</param>
/// <param name="Minor">Whether the key is minor.</param>
/// <param name="Camelot">The key on the wheel DJs use to mix songs that sound well together.</param>
/// <param name="KeyConfidence">How clear the key is, from 0 to 1.</param>
/// <param name="Energy">From 0 (calm) to 1 (intense).</param>
/// <param name="Danceability">From 0 to 1.</param>
/// <param name="Positivity">From 0 (dark) to 1 (bright and happy).</param>
/// <param name="Brightness">From 0 (dark sound) to 1 (bright sound).</param>
/// <param name="Acoustic">From 0 (electronic) to 1 (acoustic).</param>
/// <param name="Loudness">The average level, in decibels.</param>
/// <param name="Dynamics">The distance between its quiet and loud parts, in decibels.</param>
/// <param name="IntroSeconds">Where the body of the song starts.</param>
/// <param name="MixOutSeconds">Where the song starts to end, the best moment to mix into the next one.</param>
/// <param name="Seconds">The length that was listened to.</param>
/// <param name="Waveform">The shape of the song: 96 values from 0 to 100.</param>
public sealed record MusicAudioFeatures(
    double? Bpm,
    int KeyRoot,
    bool Minor,
    string Camelot,
    double KeyConfidence,
    double Energy,
    double Danceability,
    double Positivity,
    double Brightness,
    double Acoustic,
    double Loudness,
    double Dynamics,
    double IntroSeconds,
    double MixOutSeconds,
    double Seconds,
    byte[] Waveform);
