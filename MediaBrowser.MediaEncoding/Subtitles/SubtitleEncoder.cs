#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Jellyfin.Extensions;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using UtfUnknown;
using SubtitleFormat = MediaBrowser.Model.MediaInfo.SubtitleFormat;

namespace MediaBrowser.MediaEncoding.Subtitles
{
    public sealed class SubtitleEncoder : ISubtitleEncoder, IDisposable
    {
        private readonly ILogger<SubtitleEncoder> _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly ISubtitleParser _subtitleParser;
        private readonly IPathManager _pathManager;
        private readonly IServerConfigurationManager _serverConfigurationManager;

        /// <summary>
        /// The _semaphoreLocks.
        /// </summary>
        private readonly AsyncKeyedLocker<string> _semaphoreLocks = new(o =>
        {
            o.PoolSize = 20;
            o.PoolInitialFill = 1;
        });

        /// <summary>
        /// Times the playback speed at which the file is read when its subtitles are extracted. Reading only the
        /// subtitles, ffmpeg does not go faster than twice the playback speed whatever the value (measured).
        /// </summary>
        private const int ExtractionReadRate = 2;

        /// <summary>Seconds of video read at once at the start, so the first lines are there right away.</summary>
        private const int ExtractionInitialBurstSeconds = 120;

        /// <summary>ffmpeg reads its input at a limited pace from this version on.</summary>
        private static readonly Version _minFFmpegReadRate = new(5, 0);

        /// <summary>ffmpeg reads a first part at once before the limited pace from this version on.</summary>
        private static readonly Version _minFFmpegReadRateBurst = new(6, 1);

        /// <summary>A jump past the part read by less than this is left to the reading from the start.</summary>
        private static readonly TimeSpan SeekWindowLead = TimeSpan.FromSeconds(90);

        /// <summary>A reading from where the video jumped to starts this much before it.</summary>
        private static readonly TimeSpan SeekWindowBack = TimeSpan.FromSeconds(30);

        /// <summary>The readings from where the video jumped ahead, by file and subtitle stream.</summary>
        private readonly ConcurrentDictionary<string, SeekWindow> _seekWindows = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The extractions running now, by video file: whoever asks waits for the same one.</summary>
        private readonly Dictionary<string, Task> _runningExtractions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _extractionsLock = new();

        /// <summary>When the last extraction of a file failed (shown to players, cleared by a retry or a success).</summary>
        private readonly ConcurrentDictionary<string, DateTime> _failedExtractions = new(StringComparer.OrdinalIgnoreCase);

        public SubtitleEncoder(
            ILogger<SubtitleEncoder> logger,
            IFileSystem fileSystem,
            IMediaEncoder mediaEncoder,
            IHttpClientFactory httpClientFactory,
            IMediaSourceManager mediaSourceManager,
            ISubtitleParser subtitleParser,
            IPathManager pathManager,
            IServerConfigurationManager serverConfigurationManager)
        {
            _logger = logger;
            _fileSystem = fileSystem;
            _mediaEncoder = mediaEncoder;
            _httpClientFactory = httpClientFactory;
            _mediaSourceManager = mediaSourceManager;
            _subtitleParser = subtitleParser;
            _pathManager = pathManager;
            _serverConfigurationManager = serverConfigurationManager;
        }

        internal MemoryStream ConvertSubtitles(
            Stream stream,
            SubtitleInfo inputInfo,
            string outputFormat,
            long startTimeTicks,
            long endTimeTicks,
            bool preserveOriginalTimestamps)
        {
            var subtitle = _subtitleParser.Parse(stream, inputInfo.Format);

            FilterEvents(subtitle, startTimeTicks, endTimeTicks, preserveOriginalTimestamps);

            var formatter = GetWriter(outputFormat);

            var text = formatter.ToText(subtitle, "untitled");
            var bytes = Encoding.UTF8.GetBytes(text);

            return new MemoryStream(bytes, 0, bytes.Length, false, true);
        }

        internal void FilterEvents(Subtitle track, long startPositionTicks, long endTimeTicks, bool preserveTimestamps)
        {
            // Drop subs that have fully elapsed before the requested start position
            track.Paragraphs
                .RemoveAll(i => (i.StartTime.TimeSpan.Ticks - startPositionTicks) < 0 && (i.EndTime.TimeSpan.Ticks - startPositionTicks) < 0);

            if (endTimeTicks > 0)
            {
                track.Paragraphs
                    .RemoveAll(i => i.StartTime.TimeSpan.Ticks > endTimeTicks);
            }

            if (!preserveTimestamps)
            {
                foreach (var trackEvent in track.Paragraphs)
                {
                    trackEvent.StartTime = new TimeCode(TimeSpan.FromTicks(Math.Max(0, trackEvent.StartTime.TimeSpan.Ticks - startPositionTicks)));
                    trackEvent.EndTime = new TimeCode(TimeSpan.FromTicks(Math.Max(0, trackEvent.EndTime.TimeSpan.Ticks - startPositionTicks)));
                }
            }
        }

        async Task<Stream> ISubtitleEncoder.GetSubtitles(BaseItem item, string mediaSourceId, int subtitleStreamIndex, string outputFormat, long startTimeTicks, long endTimeTicks, bool preserveOriginalTimestamps, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (string.IsNullOrWhiteSpace(mediaSourceId))
            {
                throw new ArgumentNullException(nameof(mediaSourceId));
            }

            var mediaSources = await _mediaSourceManager.GetPlaybackMediaSources(item, null, true, false, cancellationToken).ConfigureAwait(false);

            var mediaSource = mediaSources
                .First(i => string.Equals(i.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));

            var subtitleStream = mediaSource.MediaStreams
               .First(i => i.Type == MediaStreamType.Subtitle && i.Index == subtitleStreamIndex);

            var (stream, info) = await GetSubtitleStream(mediaSource, subtitleStream, cancellationToken)
                        .ConfigureAwait(false);

            // Return the original if the same format is being requested
            // Character encoding was already handled in GetSubtitleStream
            // ASS is a superset of SSA, skipping the conversion and preserving the styles
            if (string.Equals(info.Format, outputFormat, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(info.Format, SubtitleFormat.SSA, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(outputFormat, SubtitleFormat.ASS, StringComparison.OrdinalIgnoreCase)))
            {
                return stream;
            }

            using (stream)
            {
                return ConvertSubtitles(stream, info, outputFormat, startTimeTicks, endTimeTicks, preserveOriginalTimestamps);
            }
        }

        /// <inheritdoc />
        public async Task<PartialSubtitles> GetPartialSubtitles(BaseItem item, string mediaSourceId, int subtitleStreamIndex, string outputFormat, bool retry, long? positionTicks, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);

            var mediaSources = await _mediaSourceManager.GetPlaybackMediaSources(item, null, true, false, cancellationToken).ConfigureAwait(false);
            var mediaSource = mediaSources.FirstOrDefault(i => string.Equals(i.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));
            var subtitleStream = mediaSource?.MediaStreams.FirstOrDefault(i => i.Type == MediaStreamType.Subtitle && i.Index == subtitleStreamIndex);

            // Only text subtitles inside the video file are read out of it
            if (mediaSource is null
                || subtitleStream is not { IsTextSubtitleStream: true, IsExtractableSubtitleStream: true }
                || (subtitleStream.IsExternal && !subtitleStream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase)))
            {
                return new PartialSubtitles(null, SubtitleExtractionState.NotNeeded);
            }

            var extension = GetExtractableSubtitleFileExtension(subtitleStream);
            var outputPath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, "." + extension);
            if (outputPath is null)
            {
                return new PartialSubtitles(null, SubtitleExtractionState.NotNeeded);
            }

            var sourcePath = string.IsNullOrEmpty(subtitleStream.Path) ? mediaSource.Path : subtitleStream.Path;
            var complete = IsCachedSubtitleFresh(outputPath, sourcePath);
            var state = complete ? SubtitleExtractionState.Complete : ExtractionStateOf(mediaSource, retry);

            // Only .srt is written in a way that can be read while it grows; the rest is shown once it is complete
            if (!string.Equals(extension, "srt", StringComparison.OrdinalIgnoreCase))
            {
                return new PartialSubtitles(null, state);
            }

            var windowKey = WindowKey(mediaSource, subtitleStream.Index);
            var subtitle = await ReadSrt(outputPath, complete, cancellationToken).ConfigureAwait(false);
            if (complete)
            {
                StopSeekWindow(windowKey);
            }
            else if (state == SubtitleExtractionState.Extracting)
            {
                // Like a player that jumps ahead: what is past the part read so far is read from where the video is
                var readUpTo = subtitle?.Paragraphs.Count > 0 ? subtitle.Paragraphs[^1].EndTime.TimeSpan.Ticks : 0;
                var window = SeekWindowFor(mediaSource, subtitleStream, windowKey, readUpTo, positionTicks);
                if (window is not null)
                {
                    var ahead = await ReadSrt(window.Path, false, cancellationToken).ConfigureAwait(false);
                    subtitle = Merge(subtitle, ahead, readUpTo);
                }
            }

            if (subtitle is null || subtitle.Paragraphs.Count == 0)
            {
                return new PartialSubtitles(null, state);
            }

            var text = GetWriter(outputFormat).ToText(subtitle, "untitled");
            return new PartialSubtitles(new MemoryStream(Encoding.UTF8.GetBytes(text), false), state);
        }

        /// <summary>
        /// Reads a .srt that ffmpeg may still be appending to; only the finished blocks count while it grows.
        /// </summary>
        /// <returns>The subtitle, or null when there is nothing yet.</returns>
        private async Task<Subtitle?> ReadSrt(string path, bool complete, CancellationToken cancellationToken)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string text;
            var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using (fileStream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(fileStream, Encoding.UTF8, true);
                text = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Replace("\r\n", "\n", StringComparison.Ordinal);
            }

            if (!complete)
            {
                var lastBlock = text.LastIndexOf("\n\n", StringComparison.Ordinal);
                text = lastBlock < 0 ? string.Empty : text[..(lastBlock + 2)];
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            return _subtitleParser.Parse(stream, "srt");
        }

        /// <summary>
        /// The lines read from the start, then the ones read from where the video jumped to (only past the first ones).
        /// </summary>
        internal static Subtitle? Merge(Subtitle? fromStart, Subtitle? ahead, long readUpTo)
        {
            if (ahead is null || ahead.Paragraphs.Count == 0)
            {
                return fromStart;
            }

            var merged = fromStart ?? new Subtitle();
            merged.Paragraphs.AddRange(ahead.Paragraphs.Where(p => p.StartTime.TimeSpan.Ticks >= readUpTo));
            merged.Paragraphs.Sort((a, b) => a.StartTime.TimeSpan.Ticks.CompareTo(b.StartTime.TimeSpan.Ticks));
            merged.Renumber();
            return merged;
        }

        private static string WindowKey(MediaSourceInfo mediaSource, int streamIndex)
            => string.Create(CultureInfo.InvariantCulture, $"{ExtractionKey(mediaSource)}|{streamIndex}");

        /// <summary>
        /// The reading from where the video is, when it is past the part read from the start (the player jumped
        /// ahead): like a streaming player, the subtitle is read from that point on, the first minutes at once.
        /// A reading already covering that point is kept; one the reading from the start caught up with is stopped.
        /// </summary>
        private SeekWindow? SeekWindowFor(MediaSourceInfo mediaSource, MediaStream subtitleStream, string key, long readUpTo, long? positionTicks)
        {
            _seekWindows.TryGetValue(key, out var current);
            if (current is not null && readUpTo >= current.StartTicks)
            {
                StopSeekWindow(key);
                current = null;
            }

            if (positionTicks is not long position || position <= readUpTo + SeekWindowLead.Ticks)
            {
                return current;
            }

            if (current is not null && position >= current.StartTicks && position <= current.CoveredUpTo() + SeekWindowLead.Ticks)
            {
                return current;
            }

            StopSeekWindow(key);
            return StartSeekWindow(mediaSource, subtitleStream, key, Math.Max(0, position - SeekWindowBack.Ticks));
        }

        private SeekWindow? StartSeekWindow(MediaSourceInfo mediaSource, MediaStream subtitleStream, string key, long fromTicks)
        {
            var path = GetSubtitleCachePath(mediaSource, subtitleStream.Index, ".ahead.srt");
            var ffmpegIndex = EncodingHelper.GetSubtitleStreamIndexForFfmpeg(mediaSource, subtitleStream);
            if (path is null || subtitleStream.IsExternal || ffmpegIndex == -1)
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var args = string.Format(
                CultureInfo.InvariantCulture,
                "-y -ss {0:0.###} -copyts {1}-i {2} -map 0:{3} -an -vn -c:s srt -flush_packets 1 \"{4}\"",
                TimeSpan.FromTicks(fromTicks).TotalSeconds,
                ThrottleArguments(),
                _mediaEncoder.GetInputPathArgument(mediaSource.Path, mediaSource),
                ffmpegIndex,
                path.EscapeProcessArgument());

            var window = new SeekWindow(fromTicks, path, new CancellationTokenSource(), DateTime.UtcNow);
            _seekWindows[key] = window;
            _logger.LogInformation("Reading subtitles of {Path} from {Position} (the video jumped ahead)", mediaSource.Path, TimeSpan.FromTicks(fromTicks));
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await RunSubtitleExtractionProcess(args, window.Cancel.Token, ThrottledDuration(mediaSource)).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Reading subtitles from {Position} failed for {Path}", TimeSpan.FromTicks(fromTicks), mediaSource.Path);
                    }
                    finally
                    {
                        // Stopped: its lines are no longer needed (the reading from the start has them)
                        if (window.Cancel.IsCancellationRequested)
                        {
                            TryDelete(path);
                            window.Cancel.Dispose();
                        }
                    }
                },
                CancellationToken.None);
            return window;
        }

        private void StopSeekWindow(string key)
        {
            if (_seekWindows.TryRemove(key, out var window))
            {
                window.Cancel.Cancel();
            }
        }

        /// <summary>The reading from the start of a file is over: the readings from further on are not needed.</summary>
        private void StopSeekWindows(string extractionKey)
        {
            foreach (var key in _seekWindows.Keys.Where(k => k.StartsWith(extractionKey + "|", StringComparison.OrdinalIgnoreCase)))
            {
                StopSeekWindow(key);
            }
        }

        private void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not delete {Path}", path);
            }
        }

        /// <summary>
        /// Where a subtitle that is not complete stands: being extracted, failed last time, or nobody started it
        /// (then it starts now, so asking for it is enough to get it ready).
        /// </summary>
        private SubtitleExtractionState ExtractionStateOf(MediaSourceInfo mediaSource, bool retry)
        {
            if (IsExtracting(mediaSource))
            {
                return SubtitleExtractionState.Extracting;
            }

            if (!_serverConfigurationManager.GetEncodingOptions().EnableSubtitleExtraction)
            {
                return SubtitleExtractionState.NotNeeded;
            }

            var key = ExtractionKey(mediaSource);
            if (retry)
            {
                _failedExtractions.TryRemove(key, out _);
            }
            else if (_failedExtractions.ContainsKey(key))
            {
                return SubtitleExtractionState.Failed;
            }

            _ = StartExtraction(mediaSource);
            return SubtitleExtractionState.Extracting;
        }

        private async Task<(Stream Stream, SubtitleInfo Info)> GetSubtitleStream(
            MediaSourceInfo mediaSource,
            MediaStream subtitleStream,
            CancellationToken cancellationToken)
        {
            var fileInfo = await GetReadableFile(mediaSource, subtitleStream, cancellationToken).ConfigureAwait(false);

            var stream = await GetSubtitleStream(fileInfo, cancellationToken).ConfigureAwait(false);

            return (stream, fileInfo);
        }

        internal async Task<Stream> GetSubtitleStream(SubtitleInfo fileInfo, CancellationToken cancellationToken)
        {
            if (fileInfo.IsExternal && MediaStream.IsTextFormat(fileInfo.Format))
            {
                var result = await DetectCharset(fileInfo.Path, cancellationToken).ConfigureAwait(false);
                var detected = result.Detected;

                var stream = fileInfo.Protocol == MediaProtocol.Http
                    ? await _httpClientFactory.CreateClient(NamedClient.Default)
                        .GetStreamAsync(new Uri(fileInfo.Path), cancellationToken)
                        .ConfigureAwait(false)
                    : AsyncFile.OpenRead(fileInfo.Path);

                // Short-circuit when the file is already UTF-8/ASCII.
                if (detected is null
                    || string.Equals(detected.EncodingName, "utf-8", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(detected.EncodingName, "ascii", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(detected.EncodingName, "us-ascii", StringComparison.OrdinalIgnoreCase))
                {
                    return stream;
                }

                _logger.LogDebug("charset {CharSet} detected for {Path}", detected.EncodingName, fileInfo.Path);

                // If encoding is not supported by .NET (e.g., ISO-8859-16), return stream as-is
                // The charset name will be passed to ffmpeg via -sub_charenc if needed
                if (detected.Encoding is null)
                {
                    _logger.LogWarning("Encoding {Encoding} is not supported by .NET for {Path}, returning stream as-is", detected.EncodingName, fileInfo.Path);
                    return stream;
                }

                await using (stream.ConfigureAwait(false))
                {
                    using var reader = new StreamReader(stream, detected.Encoding);
                    var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                    return new MemoryStream(Encoding.UTF8.GetBytes(text));
                }
            }

            return AsyncFile.OpenRead(fileInfo.Path);
        }

        internal async Task<SubtitleInfo> GetReadableFile(
            MediaSourceInfo mediaSource,
            MediaStream subtitleStream,
            CancellationToken cancellationToken)
        {
            if (!subtitleStream.IsExternal || subtitleStream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractAllExtractableSubtitles(mediaSource, cancellationToken).ConfigureAwait(false);

                var outputFileExtension = GetExtractableSubtitleFileExtension(subtitleStream);
                var outputFormat = GetExtractableSubtitleFormat(subtitleStream);
                var outputPath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, "." + outputFileExtension)
                    ?? throw new ResourceNotFoundException($"MediaSource {mediaSource.Id} has no subtitle cache (non-GUID Id, e.g. Live TV stream).");

                return new SubtitleInfo()
                {
                    Path = outputPath,
                    Protocol = MediaProtocol.File,
                    Format = outputFormat,
                    IsExternal = MediaStream.IsVobSubFormat(outputFormat)
                };
            }

            // Normalize ffmpeg codec names to the file extensions the parser is keyed on
            var currentFormat = NormalizeCodecToParserExtension((Path.GetExtension(subtitleStream.Path) ?? subtitleStream.Codec).TrimStart('.'));

            // Handle PGS subtitles as raw streams for the client to render
            if (MediaStream.IsPgsFormat(currentFormat))
            {
                return new SubtitleInfo()
                {
                    Path = subtitleStream.Path,
                    Protocol = _mediaSourceManager.GetPathProtocol(subtitleStream.Path),
                    Format = "pgssub",
                    IsExternal = true
                };
            }

            // Fallback to ffmpeg conversion
            if (!_subtitleParser.SupportsFileExtension(currentFormat))
            {
                // Convert
                var outputPath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, ".srt")
                    ?? throw new ResourceNotFoundException($"MediaSource {mediaSource.Id} has no subtitle cache (non-GUID Id, e.g. Live TV stream).");

                await ConvertTextSubtitleToSrt(subtitleStream, mediaSource, outputPath, cancellationToken).ConfigureAwait(false);

                return new SubtitleInfo()
                {
                    Path = outputPath,
                    Protocol = MediaProtocol.File,
                    Format = "srt",
                    IsExternal = true
                };
            }

            // It's possible that the subtitleStream and mediaSource don't share the same protocol (e.g. .STRM file with local subs)
            return new SubtitleInfo()
            {
                Path = subtitleStream.Path,
                Protocol = _mediaSourceManager.GetPathProtocol(subtitleStream.Path),
                Format = currentFormat,
                IsExternal = true
            };
        }

        private bool TryGetWriter(string format, [NotNullWhen(true)] out Nikse.SubtitleEdit.Core.SubtitleFormats.SubtitleFormat? value)
        {
            ArgumentException.ThrowIfNullOrEmpty(format);

            if (string.Equals(format, SubtitleFormat.ASS, StringComparison.OrdinalIgnoreCase))
            {
                value = new AdvancedSubStationAlpha();
                return true;
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                value = new JsonWriter();
                return true;
            }

            if (string.Equals(format, SubtitleFormat.SRT, StringComparison.OrdinalIgnoreCase)
                || string.Equals(format, SubtitleFormat.SUBRIP, StringComparison.OrdinalIgnoreCase))
            {
                value = new SubRip();
                return true;
            }

            if (string.Equals(format, SubtitleFormat.SSA, StringComparison.OrdinalIgnoreCase))
            {
                value = new SubStationAlpha();
                return true;
            }

            if (string.Equals(format, SubtitleFormat.VTT, StringComparison.OrdinalIgnoreCase)
                || string.Equals(format, SubtitleFormat.WEBVTT, StringComparison.OrdinalIgnoreCase))
            {
                value = new WebVTT();
                return true;
            }

            if (string.Equals(format, SubtitleFormat.TTML, StringComparison.OrdinalIgnoreCase))
            {
                value = new TimedText10();
                return true;
            }

            value = null;
            return false;
        }

        private Nikse.SubtitleEdit.Core.SubtitleFormats.SubtitleFormat GetWriter(string format)
        {
            if (TryGetWriter(format, out var writer))
            {
                return writer;
            }

            throw new ArgumentException("Unsupported format: " + format);
        }

        /// <summary>
        /// Converts the text subtitle to SRT.
        /// </summary>
        /// <param name="subtitleStream">The subtitle stream.</param>
        /// <param name="mediaSource">The input mediaSource.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        private async Task ConvertTextSubtitleToSrt(MediaStream subtitleStream, MediaSourceInfo mediaSource, string outputPath, CancellationToken cancellationToken)
        {
            using (await _semaphoreLocks.LockAsync(outputPath, cancellationToken).ConfigureAwait(false))
            {
                if (!IsCachedSubtitleFresh(outputPath, subtitleStream.Path))
                {
                    await ConvertTextSubtitleToSrtInternal(subtitleStream, mediaSource, outputPath, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // ffmpeg codec names don't always match the file extensions the subtitle parser is keyed on.
        private static string NormalizeCodecToParserExtension(string codecOrExtension)
        {
            return codecOrExtension switch
            {
                "subrip" => "srt",
                "webvtt" => "vtt",
                _ => codecOrExtension
            };
        }

        // Records "this cache was built from this exact source revision" in a sidecar file next to the cache: "<sizeBytes>:<mtimeTicks>"
        private static string GetCacheMetaPath(string cachePath) => cachePath + ".meta";

        private static string FormatCacheMeta(long length, DateTime lastWriteUtc)
            => string.Create(CultureInfo.InvariantCulture, $"{length}:{lastWriteUtc.Ticks}");

        private bool IsCachedSubtitleFresh(string cachePath, string? sourcePath)
        {
            if (!File.Exists(cachePath))
            {
                return false;
            }

            var cacheInfo = _fileSystem.GetFileInfo(cachePath);
            if (cacheInfo.Length == 0)
            {
                return false;
            }

            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                return true;
            }

            var metaPath = GetCacheMetaPath(cachePath);
            if (!File.Exists(metaPath))
            {
                // Pre-existing cache from before metadata tracking - regenerate so we can record the source state.
                return false;
            }

            try
            {
                var sourceInfo = _fileSystem.GetFileInfo(sourcePath);
                var expected = FormatCacheMeta(sourceInfo.Length, sourceInfo.LastWriteTimeUtc);
                var actual = File.ReadAllText(metaPath);
                return string.Equals(expected, actual, StringComparison.Ordinal);
            }
            catch (IOException)
            {
                return false;
            }
        }

        private void WriteCacheMeta(string cachePath, string? sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath))
            {
                return;
            }

            try
            {
                var sourceInfo = _fileSystem.GetFileInfo(sourcePath);
                if (!sourceInfo.Exists)
                {
                    return;
                }

                File.WriteAllText(GetCacheMetaPath(cachePath), FormatCacheMeta(sourceInfo.Length, sourceInfo.LastWriteTimeUtc));
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to record subtitle cache metadata for {CachePath}", cachePath);
            }
        }

        /// <summary>
        /// Converts the text subtitle to SRT internal.
        /// </summary>
        /// <param name="subtitleStream">The subtitle stream.</param>
        /// <param name="mediaSource">The input mediaSource.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        /// <exception cref="ArgumentNullException">
        /// The <c>inputPath</c> or <c>outputPath</c> is <c>null</c>.
        /// </exception>
        private async Task ConvertTextSubtitleToSrtInternal(MediaStream subtitleStream, MediaSourceInfo mediaSource, string outputPath, CancellationToken cancellationToken)
        {
            var inputPath = subtitleStream.Path;
            ArgumentException.ThrowIfNullOrEmpty(inputPath);

            ArgumentException.ThrowIfNullOrEmpty(outputPath);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath)));

            var encodingParam = await GetSubtitleFileCharacterSet(subtitleStream, subtitleStream.Language, mediaSource, cancellationToken).ConfigureAwait(false);

            // FFmpeg automatically convert character encoding when it is UTF-16
            // If we specify character encoding, it rejects with "do not specify a character encoding" and "Unable to recode subtitle event"
            if ((inputPath.EndsWith(".smi", StringComparison.Ordinal) || inputPath.EndsWith(".sami", StringComparison.Ordinal)) &&
                (encodingParam.Equals("UTF-16BE", StringComparison.OrdinalIgnoreCase) ||
                 encodingParam.Equals("UTF-16LE", StringComparison.OrdinalIgnoreCase)))
            {
                encodingParam = string.Empty;
            }
            else if (!string.IsNullOrEmpty(encodingParam))
            {
                encodingParam = " -sub_charenc " + encodingParam;
            }

            var args = string.Format(CultureInfo.InvariantCulture, "-y {0} -i \"{1}\" -c:s srt \"{2}\"", encodingParam, inputPath.EscapeProcessArgument(), outputPath.EscapeProcessArgument());

            await ExtractSubtitlesForFile(
                inputPath,
                args,
                [outputPath],
                cancellationToken).ConfigureAwait(false);

            WriteCacheMeta(outputPath, inputPath);
        }

        private string GetExtractableSubtitleFormat(MediaStream subtitleStream)
        {
            if (string.Equals(subtitleStream.Codec, "ass", StringComparison.OrdinalIgnoreCase)
                || string.Equals(subtitleStream.Codec, "ssa", StringComparison.OrdinalIgnoreCase)
                || string.Equals(subtitleStream.Codec, "pgssub", StringComparison.OrdinalIgnoreCase))
            {
                return subtitleStream.Codec;
            }
            else if (MediaStream.IsVobSubFormat(subtitleStream.Codec))
            {
                return "mks";
            }
            else
            {
                return "srt";
            }
        }

        private string GetExtractableSubtitleFileExtension(MediaStream subtitleStream)
        {
            // Using .pgssub as file extension is not allowed by ffmpeg. The file extension for pgs subtitles is .sup.
            if (string.Equals(subtitleStream.Codec, "pgssub", StringComparison.OrdinalIgnoreCase))
            {
                return "sup";
            }
            else if (MediaStream.IsVobSubFormat(subtitleStream.Codec))
            {
                // FFmpeg cannot mux VobSub subtitle streams back into the .idx/.sub pair, so we use .mks container instead.
                return "mks";
            }
            else
            {
                return GetExtractableSubtitleFormat(subtitleStream);
            }
        }

        private bool IsCodecCopyable(string codec)
        {
            return string.Equals(codec, "ass", StringComparison.OrdinalIgnoreCase)
                || string.Equals(codec, "ssa", StringComparison.OrdinalIgnoreCase)
                || string.Equals(codec, "srt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(codec, "subrip", StringComparison.OrdinalIgnoreCase)
                || string.Equals(codec, "pgssub", StringComparison.OrdinalIgnoreCase)
                || MediaStream.IsVobSubFormat(codec);
        }

        /// <inheritdoc />
        public Task ExtractAllExtractableSubtitles(MediaSourceInfo mediaSource, CancellationToken cancellationToken)
            => StartExtraction(mediaSource).WaitAsync(cancellationToken);

        /// <summary>The extraction of this file, the running one when there is one.</summary>
        private Task StartExtraction(MediaSourceInfo mediaSource)
        {
            var key = ExtractionKey(mediaSource);
            lock (_extractionsLock)
            {
                if (!_runningExtractions.TryGetValue(key, out var running))
                {
                    running = RunExtraction(key, mediaSource);
                    _runningExtractions[key] = running;
                }

                return running;
            }
        }

        private async Task RunExtraction(string key, MediaSourceInfo mediaSource)
        {
            // Registered before it goes on (the lock is held by the caller until then)
            await Task.Yield();
            try
            {
                if (await ExtractAllExtractableSubtitlesCore(mediaSource).ConfigureAwait(false))
                {
                    _failedExtractions.TryRemove(key, out _);
                }
                else
                {
                    _failedExtractions[key] = DateTime.UtcNow;
                }
            }
            finally
            {
                lock (_extractionsLock)
                {
                    _runningExtractions.Remove(key);
                }

                StopSeekWindows(key);
            }
        }

        private static string ExtractionKey(MediaSourceInfo mediaSource) => mediaSource.Path ?? mediaSource.Id;

        /// <summary>
        /// The file is read at a moderate pace, like a player streaming it (ahead of what is watched, without a burst
        /// on the disks): the first minutes at once, then a few times the playback speed.
        /// </summary>
        private string ThrottleArguments()
        {
            var version = _mediaEncoder.EncoderVersion;
            if (version is null || version < _minFFmpegReadRate)
            {
                return string.Empty;
            }

            var burst = version >= _minFFmpegReadRateBurst
                ? string.Create(CultureInfo.InvariantCulture, $"-readrate_initial_burst {ExtractionInitialBurstSeconds} ")
                : string.Empty;
            return string.Create(CultureInfo.InvariantCulture, $"-readrate {ExtractionReadRate} {burst}");
        }

        /// <summary>How long reading the whole file at that pace takes, with room to spare (zero when unknown).</summary>
        private TimeSpan ThrottledDuration(MediaSourceInfo mediaSource)
        {
            if (mediaSource.RunTimeTicks is not > 0 || _mediaEncoder.EncoderVersion is null || _mediaEncoder.EncoderVersion < _minFFmpegReadRate)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromTicks(mediaSource.RunTimeTicks.Value / ExtractionReadRate * 3 / 2) + TimeSpan.FromMinutes(5);
        }

        private bool IsExtracting(MediaSourceInfo mediaSource)
        {
            lock (_extractionsLock)
            {
                return _runningExtractions.ContainsKey(ExtractionKey(mediaSource));
            }
        }

        /// <summary>Reads the subtitles out of the file; nobody's request can cancel it (ffmpeg has its own time limit).</summary>
        /// <returns>Whether it worked.</returns>
        private async Task<bool> ExtractAllExtractableSubtitlesCore(MediaSourceInfo mediaSource)
        {
            var cancellationToken = CancellationToken.None;
            var locks = new List<IDisposable>();
            var extractableStreams = new List<MediaStream>();

            try
            {
                var subtitleStreams = mediaSource.MediaStreams
                    .Where(stream => stream is { IsExtractableSubtitleStream: true, SupportsExternalStream: true });

                foreach (var subtitleStream in subtitleStreams)
                {
                    if (subtitleStream.IsExternal
                        && !subtitleStream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var outputPath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, "." + GetExtractableSubtitleFileExtension(subtitleStream));
                    if (outputPath is null)
                    {
                        continue;
                    }

                    var releaser = await _semaphoreLocks.LockAsync(outputPath, cancellationToken).ConfigureAwait(false);

                    var sourcePath = string.IsNullOrEmpty(subtitleStream.Path) ? mediaSource.Path : subtitleStream.Path;
                    if (IsCachedSubtitleFresh(outputPath, sourcePath))
                    {
                        releaser.Dispose();
                        continue;
                    }

                    locks.Add(releaser);
                    extractableStreams.Add(subtitleStream);
                }

                if (extractableStreams.Count > 0)
                {
                    await ExtractAllExtractableSubtitlesInternal(mediaSource, extractableStreams, cancellationToken).ConfigureAwait(false);
                    await ExtractAllExtractableSubtitlesMKS(mediaSource, extractableStreams, cancellationToken).ConfigureAwait(false);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to get streams for File:{File}", mediaSource.Path);
                return false;
            }
            finally
            {
                locks.ForEach(x => x.Dispose());
            }
        }

        private async Task ExtractAllExtractableSubtitlesMKS(
           MediaSourceInfo mediaSource,
           List<MediaStream> subtitleStreams,
           CancellationToken cancellationToken)
        {
            var mksFiles = new List<string>();

            foreach (var subtitleStream in subtitleStreams)
            {
                if (string.IsNullOrEmpty(subtitleStream.Path) || !subtitleStream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!mksFiles.Contains(subtitleStream.Path))
                {
                    mksFiles.Add(subtitleStream.Path);
                }
            }

            if (mksFiles.Count == 0)
            {
                return;
            }

            foreach (string mksFile in mksFiles)
            {
                var inputPath = _mediaEncoder.GetInputArgument(mksFile, mediaSource);
                var outputPaths = new List<string>();
                var args = string.Format(
                    CultureInfo.InvariantCulture,
                    "-y {0}-i {1}",
                    ThrottleArguments(),
                    inputPath);

                foreach (var subtitleStream in subtitleStreams)
                {
                    if (!subtitleStream.Path.Equals(mksFile, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var outputPath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, "." + GetExtractableSubtitleFileExtension(subtitleStream));
                    if (outputPath is null)
                    {
                        continue;
                    }

                    var outputCodec = IsCodecCopyable(subtitleStream.Codec) ? "copy" : "srt";
                    // FFmpeg does not provide an .idx/.sub muxer, so VobSub streams must be written as MKS files.
                    var outputFormatOption = MediaStream.IsVobSubFormat(subtitleStream.Codec) ? " -f matroska" : string.Empty;
                    var streamIndex = EncodingHelper.FindIndex(mediaSource.MediaStreams, subtitleStream);

                    if (streamIndex == -1)
                    {
                        _logger.LogError("Cannot find subtitle stream index for {InputPath} ({Index}), skipping this stream", inputPath, subtitleStream.Index);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new FileNotFoundException($"Calculated path ({outputPath}) is not valid."));

                    outputPaths.Add(outputPath);
                    args += string.Format(
                        CultureInfo.InvariantCulture,
                        " -map 0:{0} -an -vn -c:s {1}{2} -flush_packets 1 \"{3}\"",
                        streamIndex,
                        outputCodec,
                        outputFormatOption,
                        outputPath.EscapeProcessArgument());
                }

                await ExtractSubtitlesForFile(inputPath, args, outputPaths, cancellationToken, ThrottledDuration(mediaSource)).ConfigureAwait(false);

                foreach (var outputPath in outputPaths)
                {
                    WriteCacheMeta(outputPath, mksFile);
                }
            }
        }

        private async Task ExtractAllExtractableSubtitlesInternal(
            MediaSourceInfo mediaSource,
            List<MediaStream> subtitleStreams,
            CancellationToken cancellationToken)
        {
            var inputPath = _mediaEncoder.GetInputPathArgument(mediaSource.Path, mediaSource);
            var outputPaths = new List<string>();
            var args = string.Format(
                CultureInfo.InvariantCulture,
                "-y {0}-i {1}",
                ThrottleArguments(),
                inputPath);

            foreach (var subtitleStream in subtitleStreams)
            {
                if (!string.IsNullOrEmpty(subtitleStream.Path) && subtitleStream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Subtitle {Index} for file {InputPath} is part in an MKS file. Skipping", inputPath, subtitleStream.Index);
                    continue;
                }

                var outputPath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, "." + GetExtractableSubtitleFileExtension(subtitleStream));
                if (outputPath is null)
                {
                    continue;
                }

                var outputCodec = IsCodecCopyable(subtitleStream.Codec) ? "copy" : "srt";
                // FFmpeg does not provide an .idx/.sub muxer, so VobSub streams must be written as MKS files.
                var outputFormatOption = MediaStream.IsVobSubFormat(subtitleStream.Codec) ? " -f matroska" : string.Empty;
                var streamIndex = EncodingHelper.GetSubtitleStreamIndexForFfmpeg(mediaSource, subtitleStream);

                if (streamIndex == -1)
                {
                    _logger.LogError("Cannot find subtitle stream index for {InputPath} ({Index}), skipping this stream", inputPath, subtitleStream.Index);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new FileNotFoundException($"Calculated path ({outputPath}) is not valid."));

                outputPaths.Add(outputPath);
                args += string.Format(
                    CultureInfo.InvariantCulture,
                    " -map 0:{0} -an -vn -c:s {1}{2} -flush_packets 1 \"{3}\"",
                    streamIndex,
                    outputCodec,
                    outputFormatOption,
                    outputPath.EscapeProcessArgument());
            }

            if (outputPaths.Count > 0)
            {
                await ExtractSubtitlesForFile(inputPath, args, outputPaths, cancellationToken, ThrottledDuration(mediaSource)).ConfigureAwait(false);

                foreach (var outputPath in outputPaths)
                {
                    WriteCacheMeta(outputPath, mediaSource.Path);
                }
            }
        }

        private async Task ExtractSubtitlesForFile(
            string inputPath,
            string args,
            IReadOnlyList<string> outputPaths,
            CancellationToken cancellationToken,
            TimeSpan minimumTimeout = default)
        {
            var (exitCode, ffmpegError) = await RunSubtitleExtractionProcess(args, cancellationToken, minimumTimeout).ConfigureAwait(false);

            var failed = false;

            if (exitCode == -1)
            {
                failed = true;

                foreach (var outputPath in outputPaths)
                {
                    try
                    {
                        _logger.LogWarning("Deleting extracted subtitle due to failure: {Path}", outputPath);
                        _fileSystem.DeleteFile(outputPath);
                    }
                    catch (FileNotFoundException)
                    {
                    }
                    catch (IOException ex)
                    {
                        _logger.LogError(ex, "Error deleting extracted subtitle {Path}", outputPath);
                    }
                }
            }
            else
            {
                foreach (var outputPath in outputPaths)
                {
                    if (!File.Exists(outputPath) || _fileSystem.GetFileInfo(outputPath).Length == 0)
                    {
                        _logger.LogError("ffmpeg subtitle extraction failed for {InputPath} to {OutputPath}", inputPath, outputPath);
                        failed = true;

                        try
                        {
                            _logger.LogWarning("Deleting extracted subtitle due to failure: {Path}", outputPath);
                            _fileSystem.DeleteFile(outputPath);
                        }
                        catch (FileNotFoundException)
                        {
                        }
                        catch (IOException ex)
                        {
                            _logger.LogError(ex, "Error deleting extracted subtitle {Path}", outputPath);
                        }

                        continue;
                    }

                    if (outputPath.EndsWith("ass", StringComparison.OrdinalIgnoreCase))
                    {
                        await SetAssFont(outputPath, cancellationToken).ConfigureAwait(false);
                    }

                    _logger.LogInformation("ffmpeg subtitle extraction completed for {InputPath} to {OutputPath}", inputPath, outputPath);
                }
            }

            if (failed)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.IsNullOrWhiteSpace(ffmpegError))
                {
                    _logger.LogError("ffmpeg subtitle extraction failed for {InputPath}: {FfmpegOutput}", inputPath, ffmpegError);
                }

                throw new FfmpegException(
                    string.Format(CultureInfo.InvariantCulture, "ffmpeg subtitle extraction failed for {0}", inputPath));
            }
        }

        /// <summary>
        /// Extracts the text subtitle.
        /// </summary>
        /// <param name="mediaSource">The mediaSource.</param>
        /// <param name="subtitleStream">The subtitle stream.</param>
        /// <param name="outputCodec">The output codec.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        /// <exception cref="ArgumentException">Must use inputPath list overload.</exception>
        private async Task ExtractTextSubtitle(
            MediaSourceInfo mediaSource,
            MediaStream subtitleStream,
            string outputCodec,
            string outputPath,
            CancellationToken cancellationToken)
        {
            using (await _semaphoreLocks.LockAsync(outputPath, cancellationToken).ConfigureAwait(false))
            {
                if (!File.Exists(outputPath) || _fileSystem.GetFileInfo(outputPath).Length == 0)
                {
                    var subtitleStreamIndex = EncodingHelper.FindIndex(mediaSource.MediaStreams, subtitleStream);

                    var args = _mediaEncoder.GetInputArgument(mediaSource.Path, mediaSource);

                    if (subtitleStream.IsExternal)
                    {
                        args = _mediaEncoder.GetExternalSubtitleInputArgument(subtitleStream.Path);
                    }

                    await ExtractTextSubtitleInternal(
                        args,
                        subtitleStreamIndex,
                        outputCodec,
                        outputPath,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task ExtractTextSubtitleInternal(
            string inputPath,
            int subtitleStreamIndex,
            string outputCodec,
            string outputPath,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(inputPath);

            ArgumentException.ThrowIfNullOrEmpty(outputPath);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath)));
            var processArgs = string.Format(
                CultureInfo.InvariantCulture,
                "-y -i {0} -copyts -map 0:{1} -an -vn -c:s {2} \"{3}\"",
                inputPath,
                subtitleStreamIndex,
                outputCodec,
                outputPath);

            await ExtractSubtitlesForFile(
                inputPath,
                processArgs,
                [outputPath],
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs ffmpeg to extract or convert subtitles, capturing its exit code and stderr output.
        /// </summary>
        /// <remarks>
        /// stdin is redirected and closed, and <c>-nostdin</c> is prepended to the arguments, so ffmpeg can never
        /// block reading an inherited stdin handle (which happens when Jellyfin runs as a service, e.g. under NSSM,
        /// and stalls subtitle extraction until the timeout). stderr is redirected and drained so a full pipe buffer
        /// cannot deadlock ffmpeg and so its output can be surfaced on failure; stdout is left un-redirected as it is
        /// unused for subtitle extraction.
        /// </remarks>
        /// <param name="arguments">The ffmpeg command line arguments.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="minimumTimeout">Time allowed at least, beyond the configured limit (a file read at a moderate pace).</param>
        /// <returns>The ffmpeg exit code (-1 on timeout) and its captured stderr output.</returns>
        private async Task<(int ExitCode, string StandardError)> RunSubtitleExtractionProcess(string arguments, CancellationToken cancellationToken, TimeSpan minimumTimeout = default)
        {
            int exitCode;
            var standardError = string.Empty;

            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    FileName = _mediaEncoder.EncoderPath,
                    Arguments = "-nostdin " + arguments,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false
                },
                EnableRaisingEvents = true
            })
            {
                _logger.LogInformation("{File} {Arguments}", process.StartInfo.FileName, process.StartInfo.Arguments);

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error starting ffmpeg");
                    throw;
                }

                // Close stdin so ffmpeg observes EOF instead of blocking on an inherited handle.
                process.StandardInput.Close();

                // Begin draining stderr before waiting for exit; a full stderr pipe buffer would otherwise deadlock ffmpeg.
                var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
                var timeoutMinutes = _serverConfigurationManager.GetEncodingOptions().SubtitleExtractionTimeoutMinutes;
                using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // A file read at a moderate pace takes longer than the configured limit allows for long videos
                var timeout = TimeSpan.FromMinutes(timeoutMinutes);
                waitSource.CancelAfter(minimumTimeout > timeout ? minimumTimeout : timeout);

                try
                {
                    await process.WaitForExitAsync(waitSource.Token).ConfigureAwait(false);
                    exitCode = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    exitCode = -1;
                }

                try
                {
                    standardError = await standardErrorTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Reading ffmpeg output was cancelled; nothing more to capture.
                }
            }

            return (exitCode, standardError);
        }

        /// <summary>
        /// Sets the ass font.
        /// </summary>
        /// <param name="file">The file.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <c>System.Threading.CancellationToken.None</c>.</param>
        /// <returns>Task.</returns>
        private async Task SetAssFont(string file, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Setting ass font within {File}", file);

            string text;
            Encoding encoding;

            using (var fileStream = AsyncFile.OpenRead(file))
            using (var reader = new StreamReader(fileStream, true))
            {
                encoding = reader.CurrentEncoding;

                text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            var newText = text.Replace(",Arial,", ",Arial Unicode MS,", StringComparison.Ordinal);

            if (!string.Equals(text, newText, StringComparison.Ordinal))
            {
                var fileStream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous);
                await using (fileStream.ConfigureAwait(false))
                {
                    var writer = new StreamWriter(fileStream, encoding);
                    await using (writer.ConfigureAwait(false))
                    {
                        await writer.WriteAsync(newText.AsMemory(), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private string? GetSubtitleCachePath(MediaSourceInfo mediaSource, int subtitleStreamIndex, string outputSubtitleExtension)
        {
            return _pathManager.GetSubtitlePath(mediaSource.Id, subtitleStreamIndex, outputSubtitleExtension);
        }

        /// <inheritdoc />
        public async Task<string> GetSubtitleFileCharacterSet(MediaStream subtitleStream, string language, MediaSourceInfo mediaSource, CancellationToken cancellationToken)
        {
            var subtitleCodec = subtitleStream.Codec;
            var path = subtitleStream.Path;

            if (path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
            {
                var cachePath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, "." + subtitleCodec);
                if (cachePath is not null)
                {
                    path = cachePath;
                    await ExtractTextSubtitle(mediaSource, subtitleStream, subtitleCodec, path, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var result = await DetectCharset(path, cancellationToken).ConfigureAwait(false);
            var charset = result.Detected?.EncodingName ?? string.Empty;

            // UTF16 is automatically converted to UTF8 by FFmpeg, do not specify a character encoding
            if ((path.EndsWith(".ass", StringComparison.Ordinal) || path.EndsWith(".ssa", StringComparison.Ordinal) || path.EndsWith(".srt", StringComparison.Ordinal))
                && (string.Equals(charset, "utf-16le", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(charset, "utf-16be", StringComparison.OrdinalIgnoreCase)))
            {
                charset = string.Empty;
            }

            _logger.LogDebug("charset {0} detected for {Path}", charset, path);

            return charset;
        }

        private async Task<DetectionResult> DetectCharset(string path, CancellationToken cancellationToken)
        {
            var protocol = _mediaSourceManager.GetPathProtocol(path);
            switch (protocol)
            {
                case MediaProtocol.Http:
                    {
                        using var stream = await _httpClientFactory
                          .CreateClient(NamedClient.Default)
                          .GetStreamAsync(new Uri(path), cancellationToken)
                          .ConfigureAwait(false);

                        return await CharsetDetector.DetectFromStreamAsync(stream, cancellationToken).ConfigureAwait(false);
                    }

                case MediaProtocol.File:
                    {
                        return await CharsetDetector.DetectFromFileAsync(path, cancellationToken)
                                              .ConfigureAwait(false);
                    }

                default:
                    throw new NotSupportedException($"Unsupported protocol: {protocol}");
            }
        }

        public async Task<string> GetSubtitleFilePath(MediaStream subtitleStream, MediaSourceInfo mediaSource, CancellationToken cancellationToken)
        {
            var info = await GetReadableFile(mediaSource, subtitleStream, cancellationToken)
                .ConfigureAwait(false);
            return info.Path;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _semaphoreLocks.Dispose();
        }

#pragma warning disable CA1034 // Nested types should not be visible
        // Only public for the unit tests
        public readonly record struct SubtitleInfo
        {
            public string Path { get; init; }

            public MediaProtocol Protocol { get; init; }

            public string Format { get; init; }

            public bool IsExternal { get; init; }
        }

        /// <summary>A reading of one subtitle from where the video jumped to.</summary>
        /// <param name="StartTicks">Where it starts in the video.</param>
        /// <param name="Path">The file it writes.</param>
        /// <param name="Cancel">Stops it.</param>
        /// <param name="StartedAt">When it started.</param>
        private sealed record SeekWindow(long StartTicks, string Path, CancellationTokenSource Cancel, DateTime StartedAt)
        {
            /// <summary>How far in the video it has read by now (the first minutes at once, then at its pace).</summary>
            /// <returns>The position, in ticks.</returns>
            public long CoveredUpTo()
                => StartTicks + TimeSpan.FromSeconds(ExtractionInitialBurstSeconds).Ticks + ((DateTime.UtcNow - StartedAt).Ticks * ExtractionReadRate);
        }
    }
}
