using System;
using System.Linq;
using MediaBrowser.MediaEncoding.Subtitles;
using Nikse.SubtitleEdit.Core.Common;
using Xunit;

namespace Jellyfin.MediaEncoding.Subtitles.Tests;

public class SubtitleSeekMergeTests
{
    private static Subtitle Lines(params int[] seconds)
    {
        var subtitle = new Subtitle();
        foreach (var s in seconds)
        {
            subtitle.Paragraphs.Add(new Paragraph($"Línea {s}", s * 1000, (s * 1000) + 2000));
        }

        return subtitle;
    }

    private static int[] StartSeconds(Subtitle? subtitle)
        => subtitle!.Paragraphs.Select(p => (int)p.StartTime.TimeSpan.TotalSeconds).ToArray();

    [Fact]
    public void Merge_ReadFromWhereTheVideoJumped_AddedAfterTheStart()
    {
        // Read from the start up to 1:00; the video jumped to 1:00:00 and that part was read from there
        var merged = SubtitleEncoder.Merge(Lines(5, 30, 58), Lines(3570, 3600, 3630), TimeSpan.FromSeconds(60).Ticks);
        Assert.Equal([5, 30, 58, 3570, 3600, 3630], StartSeconds(merged));
    }

    [Fact]
    public void Merge_LinesBothReadingsHave_OnlyOnce()
    {
        // The reading from further on started a bit before the end of the one from the start
        var merged = SubtitleEncoder.Merge(Lines(10, 20, 30), Lines(25, 30, 40), TimeSpan.FromSeconds(32).Ticks);
        Assert.Equal([10, 20, 30, 40], StartSeconds(merged));
    }

    [Fact]
    public void Merge_NothingReadFromTheStartYet_TheOtherReading()
    {
        var merged = SubtitleEncoder.Merge(null, Lines(600, 610), 0);
        Assert.Equal([600, 610], StartSeconds(merged));
        Assert.Null(SubtitleEncoder.Merge(null, null, 0));
    }
}
