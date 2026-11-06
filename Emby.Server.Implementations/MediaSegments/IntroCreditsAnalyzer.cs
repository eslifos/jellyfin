using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.MediaSegments;

/// <summary>
/// Low level detection helpers: chromaprint fingerprints, fingerprint comparison,
/// black frame analysis and chapter names.
/// </summary>
public partial class IntroCreditsAnalyzer
{
    /// <summary>
    /// Duration in seconds of one chromaprint point.
    /// </summary>
    public const double SecondsPerPoint = 0.1238;

    private const int MaxBitDifferences = 6;
    private const double MaxTimeSkip = 3.5;
    private static readonly TimeSpan _ffmpegTimeout = TimeSpan.FromMinutes(15);

    private readonly IMediaEncoder _mediaEncoder;
    private readonly IChapterManager _chapterManager;
    private readonly IntroCreditsStore _store;
    private readonly ILogger<IntroCreditsAnalyzer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroCreditsAnalyzer"/> class.
    /// </summary>
    /// <param name="mediaEncoder">The media encoder (gives the ffmpeg path).</param>
    /// <param name="chapterManager">The chapter manager.</param>
    /// <param name="store">The results store.</param>
    /// <param name="logger">The logger.</param>
    public IntroCreditsAnalyzer(IMediaEncoder mediaEncoder, IChapterManager chapterManager, IntroCreditsStore store, ILogger<IntroCreditsAnalyzer> logger)
    {
        _mediaEncoder = mediaEncoder;
        _chapterManager = chapterManager;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Gets the audio fingerprint of a part of a file, from the cache when possible.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="kind">Cache name (intro or credits).</param>
    /// <param name="startSeconds">Start of the window.</param>
    /// <param name="durationSeconds">Length of the window.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The fingerprint points, empty when the file has no usable audio.</returns>
    public async Task<uint[]> GetFingerprintAsync(BaseItem item, string kind, double startSeconds, double durationSeconds, CancellationToken cancellationToken)
    {
        var cachePath = _store.GetFingerprintPath(item.Id, kind);
        if (File.Exists(cachePath))
        {
            try
            {
                return BytesToPoints(await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false));
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not read cached fingerprint {Path}", cachePath);
            }
        }

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", item.Path,
            "-t", durationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-map", "0:a:0", "-ac", "2",
            "-f", "chromaprint", "-fp_format", "raw", "-"
        };

        var (output, error, exitCode) = await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0 || output.Length < 4)
        {
            _logger.LogDebug("Fingerprint failed for {Path} ({Kind}): {Error}", item.Path, kind, error);
            return [];
        }

        try
        {
            Directory.CreateDirectory(_store.FingerprintFolder);
            await File.WriteAllBytesAsync(cachePath, output, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not cache fingerprint {Path}", cachePath);
        }

        return BytesToPoints(output);
    }

    /// <summary>
    /// Finds the longest audio section shared by two fingerprints.
    /// </summary>
    /// <param name="lhs">Fingerprint of the episode being analyzed.</param>
    /// <param name="rhs">Fingerprint of another episode of the same season.</param>
    /// <returns>Start and end (seconds, relative to the window) of the shared section in <paramref name="lhs"/>, or null.</returns>
    public static (double Start, double End)? FindSharedSection(uint[] lhs, uint[] rhs)
    {
        if (lhs.Length == 0 || rhs.Length == 0)
        {
            return null;
        }

        // Candidate alignments come from points that appear exactly in both fingerprints.
        var rhsIndex = new Dictionary<uint, int>(rhs.Length);
        for (var i = 0; i < rhs.Length; i++)
        {
            rhsIndex[rhs[i]] = i;
        }

        var shifts = new HashSet<int>();
        for (var i = 0; i < lhs.Length; i++)
        {
            if (rhsIndex.TryGetValue(lhs[i], out var j))
            {
                shifts.Add(j - i);
            }
        }

        (double Start, double End)? best = null;
        var matches = new List<int>();
        foreach (var shift in shifts)
        {
            matches.Clear();
            var from = Math.Max(0, -shift);
            var to = Math.Min(lhs.Length, rhs.Length - shift);
            for (var i = from; i < to; i++)
            {
                if (BitOperations.PopCount(lhs[i] ^ rhs[i + shift]) <= MaxBitDifferences)
                {
                    matches.Add(i);
                }
            }

            if (matches.Count < 2)
            {
                continue;
            }

            // Longest run of matching points, allowing small gaps.
            var runStart = matches[0];
            var prev = matches[0];
            for (var k = 1; k <= matches.Count; k++)
            {
                var current = k < matches.Count ? matches[k] : int.MaxValue;
                if (k == matches.Count || (current - prev) * SecondsPerPoint > MaxTimeSkip)
                {
                    var start = runStart * SecondsPerPoint;
                    var end = (prev + 1) * SecondsPerPoint;
                    if (best is null || end - start > best.Value.End - best.Value.Start)
                    {
                        best = (start, end);
                    }

                    runStart = current;
                }

                prev = current;
            }
        }

        return best;
    }

    /// <summary>
    /// Looks for credits shown over a black background at the end of a file.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="windowStartSeconds">Where the analysis starts.</param>
    /// <param name="runtimeSeconds">The item runtime.</param>
    /// <param name="minimumCreditsSeconds">Minimum credits length to accept.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Credits start in seconds (absolute), or null.</returns>
    public async Task<double?> FindBlackFrameCreditsAsync(BaseItem item, double windowStartSeconds, double runtimeSeconds, double minimumCreditsSeconds, CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-nostats",
            "-skip_frame", "nokey",
            "-ss", windowStartSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", item.Path,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", "scale=160:-2,blackframe=amount=0:threshold=32",
            "-f", "null", "-"
        };

        var (_, error, exitCode) = await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            _logger.LogDebug("Black frame analysis failed for {Path}", item.Path);
            return null;
        }

        var frames = new List<(double Time, bool Black)>();
        foreach (Match m in BlackFrameRegex().Matches(error))
        {
            if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pblack)
                && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            {
                frames.Add((windowStartSeconds + t, pblack >= 78));
            }
        }

        if (frames.Count < 5)
        {
            return null;
        }

        frames.Sort((a, b) => a.Time.CompareTo(b.Time));
        var n = frames.Count;
        var suffixBlack = new int[n + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            suffixBlack[i] = suffixBlack[i + 1] + (frames[i].Black ? 1 : 0);
        }

        for (var i = 0; i < n; i++)
        {
            if (!frames[i].Black || runtimeSeconds - frames[i].Time < minimumCreditsSeconds)
            {
                continue;
            }

            var ratio = (double)suffixBlack[i] / (n - i);
            var local = Math.Min(8, n - i);
            var localRatio = (double)(suffixBlack[i] - suffixBlack[i + local]) / local;
            if (ratio >= 0.85 && localRatio >= 0.75)
            {
                return frames[i].Time;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds intro / credits from chapter names (Intro, Opening, Credits, Créditos...).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="runtimeSeconds">The item runtime.</param>
    /// <returns>The sections found, in seconds.</returns>
    public ((double Start, double End)? Intro, (double Start, double End)? Credits) FindChapterSections(BaseItem item, double runtimeSeconds)
    {
        var chapters = _chapterManager.GetChapters(item.Id)
            .OrderBy(c => c.StartPositionTicks)
            .ToList();
        (double Start, double End)? intro = null;
        (double Start, double End)? credits = null;

        for (var i = 0; i < chapters.Count; i++)
        {
            var name = chapters[i].Name?.Trim() ?? string.Empty;
            var start = chapters[i].StartPositionTicks / (double)TimeSpan.TicksPerSecond;
            double? next = i + 1 < chapters.Count ? chapters[i + 1].StartPositionTicks / (double)TimeSpan.TicksPerSecond : null;

            if (intro is null && IntroChapterRegex().IsMatch(name) && next.HasValue)
            {
                var length = next.Value - start;
                if (length is >= 5 and <= 200)
                {
                    intro = (start, next.Value);
                }

                continue;
            }

            if (CreditsChapterRegex().IsMatch(name) && start > runtimeSeconds / 2)
            {
                credits = (start, next ?? runtimeSeconds);
            }
        }

        return (intro, credits);
    }

    private static uint[] BytesToPoints(byte[] bytes)
    {
        var points = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, points, 0, points.Length * 4);
        return points;
    }

    private async Task<(byte[] Output, string Error, int ExitCode)> RunFfmpegAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_ffmpegTimeout);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // Not allowed on this platform; keep normal priority.
        }

        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            using var output = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return (output.ToArray(), error, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning("ffmpeg timed out while analyzing segments");
            return ([], string.Empty, -1);
        }
    }

    [GeneratedRegex(@"pblack:(\d+).*?\bt:([\d.]+)")]
    private static partial Regex BlackFrameRegex();

    [GeneratedRegex(@"^(?!.*\bcr[eé]dit)(.*\bintro(duction|ducci[oó]n)?\b.*|.*\bopening\b.*|op|apertura|cabecera|title sequence|main titles?)$", RegexOptions.IgnoreCase)]
    private static partial Regex IntroChapterRegex();

    [GeneratedRegex(@"(\bcredits?\b|cr[eé]ditos?|\bending\b|\boutro\b|end titles|^ed$)", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsChapterRegex();
}
