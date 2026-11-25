using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Emby.Server.Implementations.MusicHub;

/// <summary>
/// Tells which video of an album folder goes with a song. Files are named "19 Song - Video name-type.m4v": the music
/// video is preferred, then a video that does not say its type, the lyrics one and the live one; extras like behind the scenes are
/// never played in place of a song. Stores name the song and its video a little differently
/// ("19 17 Años (Feat. Jay) [En Vivo].m4a"), so the same track number with the same title also counts.
/// </summary>
internal static partial class MusicVideoNames
{
    private const string Separator = " - ";
    private const int MusicVideoRank = 0;
    private const int UntypedRank = 1;
    private const int LyricsRank = 2;
    private const int LiveRank = 3;

    /// <summary>A video found only by its title comes after any video with the number of the song.</summary>
    private const int ByTitleOnly = 10;

    /// <summary>Finds the best video of a song.</summary>
    /// <param name="songPath">The file of the song.</param>
    /// <param name="videos">The videos in its folder.</param>
    /// <param name="songs">The songs in the folder: a video with the number and the title of one of them is that song's.</param>
    /// <returns>The video, or null.</returns>
    public static string? Find(string songPath, IReadOnlyList<string> videos, IReadOnlyList<string> songs)
    {
        var name = Path.GetFileNameWithoutExtension(songPath);
        var song = Split(name);
        var title = song is null ? string.Empty : BareTitle(song.Value.Title);
        var sameTitle = songs
            .Select(s => Split(Path.GetFileNameWithoutExtension(s)))
            .Where(s => s is not null && BareTitle(s.Value.Title) == title)
            .Select(s => s!.Value.Number)
            .ToHashSet();

        string? best = null;
        var bestRank = int.MaxValue;
        foreach (var video in videos.OrderBy(v => Path.GetFileName(v), StringComparer.Ordinal))
        {
            var kind = Classify(Path.GetFileNameWithoutExtension(video));
            if (kind is not { } found)
            {
                continue;
            }

            var typed = found.Rank != UntypedRank;
            var rank = found.Rank;
            if (!Matches(name, song?.Number, title, found.Name, typed))
            {
                if (!IsExtraTrack(song?.Number, title, found.Name, typed, sameTitle))
                {
                    continue;
                }

                rank += ByTitleOnly;
            }

            if (rank < bestRank)
            {
                best = video;
                bestRank = rank;
            }
        }

        return best;
    }

    /// <summary>The title of a song without its edition in brackets or who it is with: "Down (Feat. X) [Deluxe]" is "Down".</summary>
    private static string BareTitle(string title) => VideoTitle(TrailingBrackets().Replace(title, string.Empty));

    /// <summary>The title of a video without who it is with; "[Live]" stays, because it is another performance.</summary>
    private static string VideoTitle(string title) => MusicNames.Comparable(Credits().Replace(title, string.Empty));

    /// <summary>
    /// Albums often add their videos as extra tracks ("26 The Funeral.m4v" for "05 The Funeral.m4a"). When another version
    /// of the song has the number of the video, the video is that version's.
    /// </summary>
    private static bool IsExtraTrack(int? number, string title, string videoName, bool typed, IReadOnlySet<int> sameTitle)
    {
        if (number is null || title.Length == 0)
        {
            return false;
        }

        var other = Split(videoName);
        if (other is null)
        {
            return IsTitle(title, videoName, typed);
        }

        return other.Value.Number != number && !sameTitle.Contains(other.Value.Number) && IsTitle(title, other.Value.Title, typed);
    }

    /// <summary>The name without its type and how good that type is; null for an extra that does not replace a song.</summary>
    private static (string Name, int Rank)? Classify(string stem)
    {
        if (ExtraType().IsMatch(stem))
        {
            return null;
        }

        var lyrics = LyricsType().Match(stem);
        if (lyrics.Success)
        {
            return (stem[..lyrics.Index].TrimEnd(), LyricsRank);
        }

        var live = LiveType().Match(stem);
        if (live.Success)
        {
            return (stem[..live.Index].TrimEnd(), LiveRank);
        }

        var video = VideoType().Match(stem);
        return video.Success ? (stem[..video.Index].TrimEnd(), MusicVideoRank) : (stem, UntypedRank);
    }

    private static bool Matches(string songName, int? number, string title, string videoName, bool typed)
    {
        if (videoName.Equals(songName, StringComparison.OrdinalIgnoreCase)
            || (typed && videoName.StartsWith(songName + Separator, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var other = Split(videoName);
        return number is not null && title.Length > 0 && other?.Number == number && IsTitle(title, other.Value.Title, typed);
    }

    /// <summary>The title of a video is all its name or, when it says its type, what comes before one of its " - ".</summary>
    private static bool IsTitle(string title, string videoTitle, bool typed)
    {
        if (VideoTitle(videoTitle) == title)
        {
            return true;
        }

        if (!typed)
        {
            return false;
        }

        for (var at = videoTitle.IndexOf(Separator, StringComparison.Ordinal); at > 0; at = videoTitle.IndexOf(Separator, at + 1, StringComparison.Ordinal))
        {
            if (VideoTitle(videoTitle[..at]) == title)
            {
                return true;
            }
        }

        return false;
    }

    private static (int Number, string Title)? Split(string name)
    {
        var match = TrackNumber().Match(name);
        return match.Success && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? (number, match.Groups[2].Value)
            : null;
    }

    [GeneratedRegex(@"^(\d{1,3})[\s._-]+(.+)$")]
    private static partial Regex TrackNumber();

    [GeneratedRegex(@"\s*\[(?:[^\[\]]|\[[^\]]*\])*\]\s*$")]
    private static partial Regex TrailingBrackets();

    [GeneratedRegex(@"\s*[(\[]\s*(?:(?:feat\.?|ft\.?|featuring|with|con)\s[^)\]]*|(?:official\s+)?(?:music\s+)?video)\s*[)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Credits();

    [GeneratedRegex(@"-\s*((official[\s_-]*)?(music[\s_-]*)?video|video[\s_-]*oficial|videoclip)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoType();

    [GeneratedRegex(@"-\s*(lyrics?([\s_-]*video)?|video[\s_-]*(con[\s_-]*)?letras?|letras?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LyricsType();

    [GeneratedRegex(@"-\s*(live|en[\s_-]*vivo)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LiveType();

    [GeneratedRegex(@"-\s*(behind[\s_-]*the[\s_-]*scenes|detr[aá]s[\s_-]*de[\s_-]*(las[\s_-]*)?(escenas?|c[aá]maras?)|making[\s_-]*of|interview|entrevista|trailer|teaser|featurette|short|scene|deleted|extras?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraType();
}
