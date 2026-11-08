using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Api.Extensions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Poster overlay badges (resolution, HDR, audio, source, edition, ratings) for cards.
/// </summary>
[Route("ItemBadges")]
[Authorize]
public partial class ItemBadgesController : BaseJellyfinApiController
{
    private const int MaxIds = 200;
    private const int EpisodesSampled = 12;

    private static readonly ConcurrentDictionary<Guid, (DateTime Saved, ItemBadges Badges)> _cache = new();

    private static readonly (string Key, string[] Names)[] _networks =
    {
        ("netflix", new[] { "netflix" }),
        ("amazon", new[] { "amazon", "prime video" }),
        ("disney", new[] { "disney+", "disney plus" }),
        ("hbo", new[] { "hbo max", "max", "hbo" }),
        ("apple", new[] { "apple tv+", "apple tv", "apple" }),
        ("hulu", new[] { "hulu" }),
        ("paramount", new[] { "paramount+", "paramount plus" }),
        ("peacock", new[] { "peacock" })
    };

    private static readonly (Regex Pattern, string Label)[] _editions =
    {
        (EditionRegex("extended"), "Extended Edition"),
        (EditionRegex("director'?s[ ._-]?cut"), "Director's Cut"),
        (EditionRegex("ultimate[ ._-]?(edition|cut)"), "Ultimate Edition"),
        (EditionRegex("final[ ._-]?cut"), "Final Cut"),
        (EditionRegex("special[ ._-]?edition"), "Special Edition"),
        (EditionRegex("criterion"), "Criterion"),
        (EditionRegex("imax"), "IMAX"),
        (EditionRegex("unrated"), "Unrated"),
        (EditionRegex("uncut"), "Uncut"),
        (EditionRegex("remastered"), "Remastered"),
        (EditionRegex("theatrical"), "Theatrical")
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemBadgesController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public ItemBadgesController(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets the overlay badges of several items.
    /// </summary>
    /// <param name="ids">Item ids (max 200).</param>
    /// <response code="200">Badges returned (items not found or not visible are skipped).</response>
    /// <returns>The badges.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<ItemBadges>> Get([FromBody] Guid[] ids)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        var result = new List<ItemBadges>();
        foreach (var id in (ids ?? Array.Empty<Guid>()).Distinct().Take(MaxIds))
        {
            var item = _libraryManager.GetItemById(id);
            if (item is null || (user is not null && !item.IsVisible(user)))
            {
                continue;
            }

            if (_cache.TryGetValue(id, out var cached) && cached.Saved == item.DateLastSaved)
            {
                result.Add(cached.Badges);
                continue;
            }

            var badges = Build(item);
            _cache[id] = (item.DateLastSaved, badges);
            result.Add(badges);
        }

        return result;
    }

    [GeneratedRegex(@"(?<![a-z])remux(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex RemuxRegex();

    [GeneratedRegex(@"(?<![a-z])(blu-?ray|bdrip|brrip|bdremux|bd25|bd50)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex BluRayRegex();

    [GeneratedRegex(@"(?<![a-z])(web-?dl|web-?rip|web|amzn|nf|dsnp|hmax|atvp)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex WebRegex();

    [GeneratedRegex(@"(?<![a-z])hdtv(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex HdtvRegex();

    [GeneratedRegex(@"(?<![a-z])(dvd|dvdrip|dvd5|dvd9)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex DvdRegex();

    private static Regex EditionRegex(string pattern) => new($"(?<![a-z]){pattern}(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private ItemBadges Build(BaseItem item)
    {
        var badges = new ItemBadges
        {
            ItemId = item.Id,
            CommunityRating = item.CommunityRating,
            CriticRating = item.CriticRating,
            ImdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb)
        };

        // Network: own studios, or the series' studios for seasons/episodes.
        var studioSource = item switch
        {
            Episode e => (BaseItem?)e.Series ?? item,
            Season s => (BaseItem?)s.Series ?? item,
            _ => item
        };
        badges.Network = MatchNetwork(studioSource.Studios);

        if (item is Episode ep)
        {
            if (ep.ParentIndexNumber.HasValue && ep.IndexNumber.HasValue)
            {
                badges.EpisodeCode = string.Create(CultureInfo.InvariantCulture, $"S{ep.ParentIndexNumber.Value:00}E{ep.IndexNumber.Value:00}");
            }
        }

        if (item is Video video)
        {
            if (item.RunTimeTicks is long ticks && ticks > 0)
            {
                badges.RuntimeMinutes = (int)Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes);
            }

            var media = Analyze(video);
            badges.Resolution = media.Resolution;
            badges.Hdr = media.Hdr;
            badges.Audio = media.Audio;
            badges.Source = media.Source;
            badges.Edition = media.Edition;
            badges.AudioLanguages = media.AudioLanguages;
            return badges;
        }

        if (item is Series or Season)
        {
            // Sample some episodes and keep the most common values.
            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = new[] { item.Id },
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                IsVirtualItem = false,
                Recursive = true,
                Limit = EpisodesSampled,
                OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) }
            }).OfType<Video>().Select(Analyze).ToList();

            if (episodes.Count > 0)
            {
                badges.Resolution = MostCommon(episodes.Select(e => e.Resolution));
                badges.Hdr = MostCommon(episodes.Select(e => e.Hdr));
                badges.Audio = MostCommon(episodes.Select(e => e.Audio));
                badges.Source = MostCommon(episodes.Select(e => e.Source));
                badges.AudioLanguages = episodes.Max(e => e.AudioLanguages);
            }
        }

        return badges;
    }

    private (string? Resolution, string? Hdr, string? Audio, string? Source, string? Edition, int AudioLanguages) Analyze(Video video)
    {
        IReadOnlyList<MediaStream> streams;
        try
        {
            streams = _mediaSourceManager.GetMediaStreams(video.Id);
        }
        catch (Exception)
        {
            streams = Array.Empty<MediaStream>();
        }

        var v = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video && !string.Equals(s.Codec, "mjpeg", StringComparison.OrdinalIgnoreCase));
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        var mainAudio = audio.FirstOrDefault(a => a.IsDefault) ?? audio.FirstOrDefault();
        var languages = audio
            .Select(a => a.Language)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !string.Equals(l, "und", StringComparison.OrdinalIgnoreCase))
            .Select(l => l!.ToLowerInvariant())
            .Distinct()
            .Count();

        var fileName = string.IsNullOrEmpty(video.Path) ? string.Empty : Path.GetFileNameWithoutExtension(video.Path);
        var folder = string.IsNullOrEmpty(video.Path) ? string.Empty : Path.GetFileName(Path.GetDirectoryName(video.Path)) ?? string.Empty;
        var names = fileName + " " + folder;

        return (Resolution(v), Hdr(v), AudioLabel(mainAudio), Source(names), Edition(names), languages);
    }

    private static string? Resolution(MediaStream? v)
    {
        if (v is null || (v.Width is null && v.Height is null))
        {
            return null;
        }

        int w = v.Width ?? 0, h = v.Height ?? 0;
        if (w >= 3200 || h >= 2000)
        {
            return "4K";
        }

        if (w >= 1800 || h >= 1000)
        {
            return "1080P";
        }

        if (w >= 1200 || h >= 700)
        {
            return "720P";
        }

        return "SD";
    }

    private static string? Hdr(MediaStream? v)
    {
        if (v is null)
        {
            return null;
        }

        return v.VideoRangeType switch
        {
            VideoRangeType.DOVI or VideoRangeType.DOVIWithHDR10 or VideoRangeType.DOVIWithHLG or VideoRangeType.DOVIWithSDR
                or VideoRangeType.DOVIWithEL or VideoRangeType.DOVIWithHDR10Plus or VideoRangeType.DOVIWithELHDR10Plus => "DV",
            VideoRangeType.HDR10Plus => "HDR10+",
            VideoRangeType.HDR10 => "HDR",
            VideoRangeType.HLG => "HLG",
            _ => null
        };
    }

    private static string? AudioLabel(MediaStream? a)
    {
        if (a is null || string.IsNullOrEmpty(a.Codec))
        {
            return null;
        }

        var codec = a.Codec.ToLowerInvariant();
        var extra = ((a.Profile ?? string.Empty) + " " + (a.Title ?? string.Empty)).ToLowerInvariant();
        var atmos = extra.Contains("atmos", StringComparison.Ordinal);
        return codec switch
        {
            "truehd" => atmos ? "TrueHD Atmos" : "TrueHD",
            "eac3" => atmos ? "DD+ Atmos" : "Dolby Digital+",
            "ac3" => "Dolby Digital",
            "dts" when extra.Contains("dts:x", StringComparison.Ordinal) || extra.Contains("dts-x", StringComparison.Ordinal) => "DTS:X",
            "dts" when extra.Contains("ma", StringComparison.Ordinal) => "DTS-HD MA",
            "dts" when extra.Contains("hd", StringComparison.Ordinal) => "DTS-HD",
            "dts" => "DTS",
            "aac" => "AAC",
            "flac" => "FLAC",
            "opus" => "Opus",
            "mp3" => "MP3",
            "vorbis" => "Vorbis",
            _ when codec.StartsWith("pcm", StringComparison.Ordinal) => "PCM",
            _ => a.Codec.ToUpperInvariant()
        };
    }

    private static string? Source(string names)
    {
        if (string.IsNullOrWhiteSpace(names))
        {
            return null;
        }

        if (RemuxRegex().IsMatch(names))
        {
            return "REMUX";
        }

        if (BluRayRegex().IsMatch(names))
        {
            return "BLU-RAY";
        }

        if (WebRegex().IsMatch(names))
        {
            return "WEB";
        }

        if (HdtvRegex().IsMatch(names))
        {
            return "HDTV";
        }

        return DvdRegex().IsMatch(names) ? "DVD" : null;
    }

    private static string? Edition(string names)
    {
        foreach (var (pattern, label) in _editions)
        {
            if (pattern.IsMatch(names))
            {
                return label;
            }
        }

        return null;
    }

    private static string? MatchNetwork(string[]? studios)
    {
        if (studios is null || studios.Length == 0)
        {
            return null;
        }

        foreach (var studio in studios)
        {
            var s = studio.Trim().ToLowerInvariant();
            foreach (var (key, names) in _networks)
            {
                if (names.Any(n => s == n || s.StartsWith(n + " ", StringComparison.Ordinal)))
                {
                    return key;
                }
            }
        }

        return null;
    }

    private static string? MostCommon(IEnumerable<string?> values)
        => values.Where(v => v is not null).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
}
