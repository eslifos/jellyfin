using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.MediaSegments;

/// <summary>
/// Detects intros and credits for whole seasons (comparing episodes) and for movies.
/// </summary>
public class IntroCreditsDetector
{
    /// <summary>
    /// Version of the detection logic. Records with an older version are analyzed again.
    /// </summary>
    public const int Version = 1;

    private const double IntroMinSeconds = 15;
    private const double IntroMaxSeconds = 150;
    private const double IntroWindowMaxSeconds = 600;
    private const double CreditsMinSeconds = 15;
    private const double CreditsMaxSeconds = 420;
    private const double EpisodeCreditsWindowMaxSeconds = 420;
    private const double MovieCreditsWindowMaxSeconds = 900;
    private const double MovieCreditsMinSeconds = 30;

    private readonly IntroCreditsAnalyzer _analyzer;
    private readonly ILogger<IntroCreditsDetector> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroCreditsDetector"/> class.
    /// </summary>
    /// <param name="analyzer">The analyzer.</param>
    /// <param name="logger">The logger.</param>
    public IntroCreditsDetector(IntroCreditsAnalyzer analyzer, ILogger<IntroCreditsDetector> logger)
    {
        _analyzer = analyzer;
        _logger = logger;
    }

    /// <summary>
    /// Analyzes episodes of one season.
    /// </summary>
    /// <param name="episodes">All episodes of the season (used as references).</param>
    /// <param name="targets">The episodes that need a result.</param>
    /// <param name="maxParallel">How many ffmpeg processes may run at once.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of each target episode.</returns>
    public async Task<Dictionary<Guid, IntroCreditsRecord>> AnalyzeSeasonAsync(
        IReadOnlyList<Episode> episodes,
        IReadOnlySet<Guid> targets,
        int maxParallel,
        CancellationToken cancellationToken)
    {
        var ordered = episodes
            .Where(e => (e.RunTimeTicks ?? 0) > 0)
            .OrderBy(e => e.ParentIndexNumber ?? 0)
            .ThenBy(e => e.IndexNumber ?? int.MaxValue)
            .ThenBy(e => e.SortName, StringComparer.Ordinal)
            .ToList();

        // Each target is compared with its closest episodes.
        var references = new Dictionary<Guid, List<Episode>>();
        var needed = new Dictionary<Guid, Episode>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var episode = ordered[i];
            if (!targets.Contains(episode.Id))
            {
                continue;
            }

            needed[episode.Id] = episode;
            var refs = new List<Episode>();
            foreach (var offset in new[] { 1, -1, 2, -2 })
            {
                var j = i + offset;
                if (j >= 0 && j < ordered.Count && refs.Count < 3)
                {
                    refs.Add(ordered[j]);
                    needed[ordered[j].Id] = ordered[j];
                }
            }

            references[episode.Id] = refs;
        }

        var introPrints = new ConcurrentDictionary<Guid, uint[]>();
        var creditPrints = new ConcurrentDictionary<Guid, uint[]>();
        if (ordered.Count > 1)
        {
            await Parallel.ForEachAsync(
                needed.Values,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = cancellationToken },
                async (episode, ct) =>
                {
                    var runtime = Seconds(episode.RunTimeTicks);
                    var introWindow = Math.Min(runtime * 0.25, IntroWindowMaxSeconds);
                    var creditsWindow = Math.Min(runtime * 0.3, EpisodeCreditsWindowMaxSeconds);
                    introPrints[episode.Id] = await _analyzer.GetFingerprintAsync(episode, "intro", 0, introWindow, ct).ConfigureAwait(false);
                    creditPrints[episode.Id] = await _analyzer.GetFingerprintAsync(episode, "credits", runtime - creditsWindow, creditsWindow, ct).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }

        var results = new Dictionary<Guid, IntroCreditsRecord>();
        foreach (var (id, refs) in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episode = needed[id];
            var runtime = Seconds(episode.RunTimeTicks);
            var record = NewRecord(episode);

            var (chapterIntro, chapterCredits) = _analyzer.FindChapterSections(episode, runtime);
            if (chapterIntro.HasValue)
            {
                SetIntro(record, chapterIntro.Value, "Chapter");
            }

            if (chapterCredits.HasValue)
            {
                SetCredits(record, chapterCredits.Value, "Chapter");
            }

            if (record.IntroStartTicks is null && introPrints.TryGetValue(id, out var introPrint))
            {
                var best = BestMatch(introPrint, refs.Select(r => introPrints.GetValueOrDefault(r.Id)), IntroMinSeconds, IntroMaxSeconds);
                if (best.HasValue)
                {
                    // Intros that start a few seconds in are snapped to the beginning.
                    var start = best.Value.Start < 2 ? 0 : best.Value.Start;
                    SetIntro(record, (start, best.Value.End), "Fingerprint");
                }
            }

            if (record.CreditsStartTicks is null && creditPrints.TryGetValue(id, out var creditPrint))
            {
                var best = BestMatch(creditPrint, refs.Select(r => creditPrints.GetValueOrDefault(r.Id)), CreditsMinSeconds, CreditsMaxSeconds);
                if (best.HasValue)
                {
                    var windowStart = runtime - Math.Min(runtime * 0.3, EpisodeCreditsWindowMaxSeconds);
                    var start = windowStart + best.Value.Start;
                    var end = windowStart + best.Value.End;
                    if (runtime - end < 10)
                    {
                        end = runtime;
                    }

                    SetCredits(record, (start, end), "Fingerprint");
                }
            }

            if (record.CreditsStartTicks is null)
            {
                var windowStart = runtime - Math.Min(runtime * 0.3, EpisodeCreditsWindowMaxSeconds);
                var start = await _analyzer.FindBlackFrameCreditsAsync(episode, windowStart, runtime, CreditsMinSeconds, cancellationToken).ConfigureAwait(false);
                if (start.HasValue)
                {
                    SetCredits(record, (start.Value, runtime), "BlackFrames");
                }
            }

            _logger.LogDebug(
                "Intro/credits for {Name}: intro {IntroSource}, credits {CreditsSource}",
                episode.Name,
                record.IntroSource ?? "none",
                record.CreditsSource ?? "none");
            results[id] = record;
        }

        return results;
    }

    /// <summary>
    /// Analyzes a movie (chapters first, then credits over a black background).
    /// </summary>
    /// <param name="item">The movie.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<IntroCreditsRecord> AnalyzeMovieAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var runtime = Seconds(item.RunTimeTicks);
        var record = NewRecord(item);
        if (runtime <= 0)
        {
            return record;
        }

        var (chapterIntro, chapterCredits) = _analyzer.FindChapterSections(item, runtime);
        if (chapterIntro.HasValue)
        {
            SetIntro(record, chapterIntro.Value, "Chapter");
        }

        if (chapterCredits.HasValue)
        {
            SetCredits(record, chapterCredits.Value, "Chapter");
        }
        else
        {
            var windowStart = runtime - Math.Min(runtime * 0.2, MovieCreditsWindowMaxSeconds);
            var start = await _analyzer.FindBlackFrameCreditsAsync(item, windowStart, runtime, MovieCreditsMinSeconds, cancellationToken).ConfigureAwait(false);
            if (start.HasValue)
            {
                SetCredits(record, (start.Value, runtime), "BlackFrames");
            }
        }

        return record;
    }

    private static (double Start, double End)? BestMatch(uint[] print, IEnumerable<uint[]?> others, double minSeconds, double maxSeconds)
    {
        (double Start, double End)? best = null;
        foreach (var other in others)
        {
            if (other is null || other.Length == 0)
            {
                continue;
            }

            var match = IntroCreditsAnalyzer.FindSharedSection(print, other);
            if (match is null)
            {
                continue;
            }

            var length = match.Value.End - match.Value.Start;
            if (length < minSeconds || length > maxSeconds)
            {
                continue;
            }

            if (best is null || length > best.Value.End - best.Value.Start)
            {
                best = match;
            }
        }

        return best;
    }

    private static IntroCreditsRecord NewRecord(BaseItem item) => new()
    {
        Version = Version,
        AnalyzedUtc = DateTime.UtcNow,
        RunTimeTicks = item.RunTimeTicks ?? 0
    };

    private static void SetIntro(IntroCreditsRecord record, (double Start, double End) section, string source)
    {
        record.IntroStartTicks = Ticks(section.Start);
        record.IntroEndTicks = Ticks(section.End);
        record.IntroSource = source;
    }

    private static void SetCredits(IntroCreditsRecord record, (double Start, double End) section, string source)
    {
        record.CreditsStartTicks = Ticks(section.Start);
        record.CreditsEndTicks = Ticks(section.End);
        record.CreditsSource = source;
    }

    private static double Seconds(long? ticks) => (ticks ?? 0) / (double)TimeSpan.TicksPerSecond;

    private static long Ticks(double seconds) => (long)(Math.Max(0, seconds) * TimeSpan.TicksPerSecond);
}
