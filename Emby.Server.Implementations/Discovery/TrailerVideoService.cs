using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Discovery;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Discovery;

/// <summary>
/// Downloads trailers with yt-dlp (H.264 + AAC in MP4, up to 1080p) into the cache and keeps the cache under a size limit.
/// </summary>
public sealed partial class TrailerVideoService : ITrailerVideoService, IDisposable
{
    private const long MaxCacheBytes = 4L * 1024 * 1024 * 1024;
    private const string FileSuffix = ".v3.mp4";
    private const string FirstChoice = "bv*[height<=1080][vcodec^=avc1]+ba[ext=m4a]/bv*[height<=1080][vcodec^=avc1]+ba/b[vcodec^=avc1][acodec!=none]/bv*[height<=1080]+ba/b[acodec!=none]";
    private const string WithAudioOnly = "b[vcodec^=avc1][acodec!=none]/b[acodec!=none][vcodec!=none][ext=mp4]/b[acodec!=none][vcodec!=none]";
    private static readonly TimeSpan _downloadTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan _updateEvery = TimeSpan.FromDays(1);

    private readonly IApplicationPaths _paths;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<TrailerVideoService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _downloads = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private DateTime _lastUpdate = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, DateTime> _failed = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerVideoService"/> class.
    /// </summary>
    /// <param name="paths">The application paths.</param>
    /// <param name="mediaEncoder">The media encoder (for the ffmpeg path).</param>
    /// <param name="logger">The logger.</param>
    public TrailerVideoService(IApplicationPaths paths, IMediaEncoder mediaEncoder, ILogger<TrailerVideoService> logger)
    {
        _paths = paths;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    private string CacheDir => Path.Combine(_paths.CachePath, "trailers");

    /// <inheritdoc />
    public string? GetCachedVideo(string key)
    {
        if (!IsValidKey(key))
        {
            return null;
        }

        var file = Path.Combine(CacheDir, key + FileSuffix);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            File.SetLastAccessTimeUtc(file, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // Not important
        }

        return file;
    }

    /// <inheritdoc />
    public async Task<string?> GetVideoAsync(string key, CancellationToken cancellationToken)
    {
        if (GetCachedVideo(key) is string cached)
        {
            return cached;
        }

        if (!IsValidKey(key))
        {
            return null;
        }

        // A video that just failed is not tried again for 6 hours
        if (_failed.TryGetValue(key, out var failedAt) && DateTime.UtcNow - failedAt < TimeSpan.FromHours(6))
        {
            return null;
        }

        // One download per video, shared by everyone asking for it; the download is not cancelled when a viewer leaves
        var lazy = _downloads.GetOrAdd(key, k => new Lazy<Task<string?>>(() => DownloadAsync(k)));
        try
        {
            var result = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                _failed[key] = DateTime.UtcNow;
            }

            return result;
        }
        finally
        {
            if (lazy.Value.IsCompleted)
            {
                _downloads.TryRemove(key, out _);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _updateLock.Dispose();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{6,20}$")]
    private static partial Regex KeyRegex();

    private static bool IsValidKey(string key) => !string.IsNullOrEmpty(key) && KeyRegex().IsMatch(key);

    private async Task<string?> DownloadAsync(string key)
    {
        Directory.CreateDirectory(CacheDir);
        await UpdateToolAsync().ConfigureAwait(false);

        var work = Path.Combine(CacheDir, "tmp-" + key);
        var target = Path.Combine(CacheDir, key + FileSuffix);
        try
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, true);
            }

            Directory.CreateDirectory(work);

            var file = await TryDownloadAsync(key, work, FirstChoice).ConfigureAwait(false);
            var streams = file is null ? null : await ProbeAsync(file).ConfigureAwait(false);
            if (file is null || streams is null || !streams.Value.HasAudio)
            {
                // Some videos come without a usable audio track with the first choice: take a format that has both
                _logger.LogInformation("Trailer {Key}: no audio with the first format, trying another one", key);
                Directory.Delete(work, true);
                Directory.CreateDirectory(work);
                file = await TryDownloadAsync(key, work, WithAudioOnly).ConfigureAwait(false);
                streams = file is null ? null : await ProbeAsync(file).ConfigureAwait(false);
            }

            if (file is null || streams is null)
            {
                return null;
            }

            var h264 = string.Equals(streams.Value.Video, "h264", StringComparison.OrdinalIgnoreCase);
            if (!h264 || (streams.Value.HasAudio && !streams.Value.BrowserAudio))
            {
                // AV1 / VP9 pictures and Opus / Vorbis sound do not play in every browser: convert to H.264 (up to 1080p) + AAC
                var converted = Path.Combine(work, "converted.mp4");
                var args = new List<string> { "-y", "-v", "error", "-i", file, "-map", "0:v:0" };
                if (streams.Value.HasAudio)
                {
                    args.AddRange(["-map", "0:a:0"]);
                }

                args.AddRange(h264
                    ? ["-c:v", "copy"]
                    : ["-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-pix_fmt", "yuv420p", "-vf", "scale=-2:'min(1080,ih)'"]);
                args.AddRange(streams.Value.BrowserAudio ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "192k"]);
                args.AddRange(["-movflags", "+faststart", converted]);
                _logger.LogInformation("Trailer {Key}: converting {Video} to H.264 / AAC", key, streams.Value.Video);
                var (okConvert, convertOutput) = await RunAsync(
                    _mediaEncoder.EncoderPath ?? "ffmpeg",
                    [.. args],
                    TimeSpan.FromMinutes(15)).ConfigureAwait(false);
                if (okConvert && File.Exists(converted))
                {
                    file = converted;
                }
                else
                {
                    _logger.LogWarning("Trailer {Key} could not be converted: {Output}", key, convertOutput);
                }
            }

            if (!streams.Value.HasAudio)
            {
                _logger.LogWarning("Trailer {Key} has no sound in any format", key);
            }

            File.Move(file, target, true);
            _logger.LogInformation("Trailer {Key} downloaded ({Size:N0} KB)", key, new FileInfo(target).Length / 1024);
            Trim();
            return target;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trailer {Key} could not be downloaded", key);
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, true);
                }
            }
            catch (IOException)
            {
                // Cleaned up next time
            }
        }
    }

    private async Task<string?> TryDownloadAsync(string key, string work, string format)
    {
        var args = new[]
        {
            "--no-playlist", "--no-warnings", "--no-progress", "--no-part",
            "-f", format,
            "--merge-output-format", "mp4",
            "--ffmpeg-location", _mediaEncoder.EncoderPath ?? "ffmpeg",
            "-o", Path.Combine(work, "video.%(ext)s"),
            "https://www.youtube.com/watch?v=" + key
        };

        var (ok, output) = await RunAsync(YtDlpPath, args, _downloadTimeout).ConfigureAwait(false);
        var file = Directory.EnumerateFiles(work, "video.*").FirstOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        if (!ok || file is null)
        {
            _logger.LogWarning("Trailer {Key} could not be downloaded ({Format}): {Output}", key, format, output);
            return null;
        }

        return file;
    }

    /// <summary>
    /// Reads which streams a file has: whether there is sound and whether a browser plays it in MP4 (AAC / MP3).
    /// </summary>
    private async Task<(bool HasAudio, bool BrowserAudio, string? Video)?> ProbeAsync(string file)
    {
        var (ok, output) = await RunAsync(
            _mediaEncoder.ProbePath ?? "ffprobe",
            ["-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "csv=p=0", file],
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!ok)
        {
            return null;
        }

        var streams = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(','))
            .Where(p => p.Length >= 2)
            .ToList();
        var audio = streams.Where(p => p[1] == "audio").Select(p => p[0]).ToList();
        var video = streams.FirstOrDefault(p => p[1] == "video")?[0];
        return (audio.Count > 0, audio.Count > 0 && (audio[0] == "aac" || audio[0] == "mp3"), video);
    }

    private static string YtDlpPath => Environment.GetEnvironmentVariable("JELLYFIN_YTDLP_PATH") is { Length: > 0 } path ? path : "yt-dlp";

    /// <summary>
    /// YouTube changes often: keep yt-dlp up to date (once a day, when it can update itself).
    /// </summary>
    private async Task UpdateToolAsync()
    {
        if (DateTime.UtcNow - _lastUpdate < _updateEvery)
        {
            return;
        }

        await _updateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow - _lastUpdate < _updateEvery)
            {
                return;
            }

            _lastUpdate = DateTime.UtcNow;
            var (_, output) = await RunAsync(YtDlpPath, ["-U"], TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            _logger.LogDebug("yt-dlp update: {Output}", output);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private static async Task<(bool Ok, string Output)> RunAsync(string program, string[] args, TimeSpan timeout)
    {
        var info = new ProcessStartInfo
        {
            FileName = program,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (false, program + " could not be started: " + ex.Message);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // Already gone
            }

            return (false, "timeout");
        }

        var output = (await stderr.ConfigureAwait(false)) + (await stdout.ConfigureAwait(false));
        return (process.ExitCode == 0, output.Length > 1500 ? output[^1500..] : output);
    }

    /// <summary>
    /// Deletes the least recently watched trailers when the cache gets too big.
    /// </summary>
    private void Trim()
    {
        foreach (var old in new DirectoryInfo(CacheDir).GetFiles("*.mp4").Where(f => !f.Name.EndsWith(FileSuffix, StringComparison.Ordinal)))
        {
            try
            {
                old.Delete();
            }
            catch (IOException)
            {
                // In use; next time
            }
        }

        var files = new DirectoryInfo(CacheDir).GetFiles("*" + FileSuffix)
            .OrderByDescending(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc)
            .ToList();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (total > MaxCacheBytes)
            {
                try
                {
                    file.Delete();
                }
                catch (IOException)
                {
                    // In use; next time
                }
            }
        }
    }
}
