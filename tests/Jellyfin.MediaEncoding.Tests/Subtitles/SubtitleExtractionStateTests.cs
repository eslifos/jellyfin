using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Subtitles.Tests;

public sealed class SubtitleExtractionStateTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "subtitle-state-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MediaSourceInfo _source;
    private readonly SubtitleEncoder _encoder;

    public SubtitleExtractionStateTests()
    {
        Directory.CreateDirectory(_cache);
        _source = new MediaSourceInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            // A file that is not there: the cache needs no freshness check and ffmpeg cannot read it
            Path = Path.Combine(_cache, "missing.mkv"),
            MediaStreams =
            [
                new MediaStream { Type = MediaStreamType.Video, Index = 0 },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 2, Codec = "subrip", IsExternal = false, SupportsExternalStream = true },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 3, Codec = "ass", IsExternal = false, SupportsExternalStream = true },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 4, Codec = "pgssub", IsExternal = false, SupportsExternalStream = true },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 5, Codec = "subrip", IsExternal = true, Path = Path.Combine(_cache, "movie.es.srt"), SupportsExternalStream = true }
            ]
        };

        var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject<ISubtitleParser>(new SubtitleEditParser(NullLogger<SubtitleEditParser>.Instance));
        fixture.Freeze<Mock<IPathManager>>()
            .Setup(p => p.GetSubtitlePath(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns((string id, int index, string extension) => Path.Combine(_cache, $"{id}_{index}{extension}"));
        fixture.Freeze<Mock<IServerConfigurationManager>>()
            .Setup(c => c.GetConfiguration("encoding"))
            .Returns(new EncodingOptions { EnableSubtitleExtraction = true, SubtitleExtractionTimeoutMinutes = 1 });
        // ffmpeg is not there: every extraction fails
        fixture.Freeze<Mock<IMediaEncoder>>().SetupGet(e => e.EncoderPath).Returns(Path.Combine(_cache, "no-ffmpeg"));
        fixture.Freeze<Mock<IMediaSourceManager>>()
            .Setup(m => m.GetPlaybackMediaSources(It.IsAny<BaseItem>(), It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MediaSourceInfo> { _source });
        _encoder = fixture.Create<SubtitleEncoder>();
    }

    public void Dispose() => Directory.Delete(_cache, true);

    private string CachePath(int index, string extension) => Path.Combine(_cache, $"{_source.Id}_{index}.{extension}");

    private void WriteCache()
    {
        File.WriteAllText(CachePath(2, "srt"), "1\n00:00:01,000 --> 00:00:02,000\nHola\n\n");
        File.WriteAllText(CachePath(3, "ass"), "[Script Info]\n");
    }

    [Fact]
    public async Task GetPartialSubtitles_ExternalFile_NotNeeded()
    {
        var partial = await _encoder.GetPartialSubtitles(new Movie(), _source.Id, 5, "json", false, null, CancellationToken.None);
        Assert.Equal(SubtitleExtractionState.NotNeeded, partial.State);
        Assert.Null(partial.Content);
    }

    [Fact]
    public async Task GetPartialSubtitles_Extracted_CompleteWithContent()
    {
        WriteCache();
        var partial = await _encoder.GetPartialSubtitles(new Movie(), _source.Id, 2, "json", false, null, CancellationToken.None);
        Assert.Equal(SubtitleExtractionState.Complete, partial.State);
        Assert.NotNull(partial.Content);
    }

    [Fact]
    public async Task GetPartialSubtitles_ExtractionFails_FailedThenRetried()
    {
        // Nobody extracted it: asking starts it
        var first = await _encoder.GetPartialSubtitles(new Movie(), _source.Id, 2, "json", false, null, CancellationToken.None);
        Assert.Equal(SubtitleExtractionState.Extracting, first.State);

        // It fails (no ffmpeg); the next question says so instead of starting it again
        var state = await WaitForState(SubtitleExtractionState.Failed, retry: false);
        Assert.Equal(SubtitleExtractionState.Failed, state);

        // A retry starts it again
        var retried = await _encoder.GetPartialSubtitles(new Movie(), _source.Id, 2, "json", true, null, CancellationToken.None);
        Assert.Equal(SubtitleExtractionState.Extracting, retried.State);
        Assert.Equal(SubtitleExtractionState.Failed, await WaitForState(SubtitleExtractionState.Failed, retry: false));
    }

    [Fact]
    public async Task ExtractAllExtractableSubtitles_WaitingCancelled_ExtractionGoesOn()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _encoder.ExtractAllExtractableSubtitles(_source, cancelled.Token));

        // The extraction itself ran to its end (and failed, without ffmpeg)
        Assert.Equal(SubtitleExtractionState.Failed, await WaitForState(SubtitleExtractionState.Failed, retry: false));
    }

    private async Task<SubtitleExtractionState> WaitForState(SubtitleExtractionState expected, bool retry)
    {
        var state = SubtitleExtractionState.NotNeeded;
        for (var i = 0; i < 100; i++)
        {
            state = (await _encoder.GetPartialSubtitles(new Movie(), _source.Id, 2, "json", retry, null, CancellationToken.None)).State;
            if (state == expected)
            {
                break;
            }

            await Task.Delay(50);
        }

        return state;
    }
}
