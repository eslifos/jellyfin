using Emby.Server.Implementations.Discovery;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Discovery;

public class TmdbImageCacheTests
{
    [Theory]
    [InlineData("w92", "zhB5CHEgqqh4wnEqDNJLfWXJlcL.jpg")]
    [InlineData("w185", "abc_DEF-123.jpeg")]
    [InlineData("w300", "still.png")]
    [InlineData("w780", "a.jpg")]
    public void IsValid_TmdbSizeAndFile_True(string size, string file)
    {
        Assert.True(TmdbImageCache.IsValid(size, file));
    }

    [Theory]
    [InlineData("original", "abc.jpg")] // the heavy size is never fetched
    [InlineData("w1280", "abc.jpg")]
    [InlineData("W92", "abc.jpg")]
    [InlineData("w92", "../config.xml")] // nothing outside the cache folder
    [InlineData("w92", "..\\secret.jpg")]
    [InlineData("w92", "sub/abc.jpg")]
    [InlineData("w92", "abc.svg")]
    [InlineData("w92", "abc.jpg?x=1")]
    [InlineData("w92", ".jpg")]
    [InlineData("w92", "")]
    [InlineData(null, "abc.jpg")]
    [InlineData("w92", null)]
    public void IsValid_AnythingElse_False(string? size, string? file)
    {
        Assert.False(TmdbImageCache.IsValid(size, file));
    }

    [Fact]
    public void IsValid_VeryLongName_False()
    {
        Assert.False(TmdbImageCache.IsValid("w92", new string('a', 65) + ".jpg"));
    }
}
