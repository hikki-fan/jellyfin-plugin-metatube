using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

public sealed class SubtitleMatcherTests
{
    private const string UnbadgedUrl =
        "http://metatube:8080/v1/images/primary/JavDB/DE6Xa?ratio=-1&pos=-1&auto=False&quality=90";

    private const string BadgedUrl =
        "http://metatube:8080/v1/images/primary/JavDB/DE6Xa?ratio=-1&pos=-1&auto=False&badge=zimu.png&quality=90";

    [Theory]
    [InlineData("MIDE-949-C.mp4", true)]
    [InlineData("MIDE-949-C.mp4.strm", true)]
    [InlineData("ABF-120-UC.mp4.strm", true)]
    [InlineData("ABC-123-CH.rmvb.strm", true)]
    [InlineData("ABC-123.mp4.strm", false)]
    [InlineData("ABC-123-CUSTOM.mp4.strm", false)]
    public void HasEmbeddedChineseSubtitle_HandlesNestedStrmNames(string filename, bool expected)
    {
        Assert.Equal(expected, SubtitleMatcher.HasEmbeddedChineseSubtitle(filename));
    }

    [Theory]
    [MemberData(nameof(ExternalSubtitleCases))]
    public void HasExternalChineseSubtitle_NormalizesMediaExtensions(
        string basename,
        string[] files,
        bool expected)
    {
        Assert.Equal(expected, SubtitleMatcher.HasExternalChineseSubtitle(basename, files));
    }

    public static TheoryData<string, string[], bool> ExternalSubtitleCases => new()
    {
        { "TITLE-C.mp4", new[] { "TITLE-C.chs.srt" }, true },
        { "TITLE-C.mp4", new[] { "TITLE-C.mp4.chs.srt" }, true },
        { "TITLE.rmvb", new[] { "TITLE.zh-cn.ass" }, true },
        { "TITLE.mp4.strm", new[] { "TITLE.mp4.zho.srt" }, true },
        { "TITLE.mp4", new[] { "OTHER.chs.srt" }, false }
    };

    [Theory]
    [InlineData(BadgedUrl, true)]
    [InlineData(UnbadgedUrl, false)]
    [InlineData("https://example.com/v1/images/primary/JavDB/DE6Xa?badge=zimu.png", false)]
    [InlineData("http://metatube:8080/v1/images/primary/JavDB/OTHER?badge=zimu.png", false)]
    [InlineData("/media/poster.jpg", false)]
    public void IsBadgedVersionOf_RequiresExactOwnedImage(string currentImage, bool expected)
    {
        Assert.Equal(expected, SubtitleMatcher.IsBadgedVersionOf(currentImage, UnbadgedUrl));
    }
}
