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

    [Fact]
    public void ShouldReconcilePrimaryImage_AddsMissingBadge()
    {
        var changed = SubtitleMatcher.ShouldReconcilePrimaryImage(
            "/media/poster.jpg", BadgedUrl, UnbadgedUrl, true, true, out var target);

        Assert.True(changed);
        Assert.Equal(BadgedUrl, target);
    }

    [Fact]
    public void ShouldReconcilePrimaryImage_IsIdempotentAfterSuccess()
    {
        var changed = SubtitleMatcher.ShouldReconcilePrimaryImage(
            BadgedUrl, BadgedUrl, UnbadgedUrl, true, true, out var target);

        Assert.False(changed);
        Assert.Null(target);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ShouldReconcilePrimaryImage_RemovesOwnedBadgeOnly(
        bool hasSubtitle,
        bool enableBadges)
    {
        var changed = SubtitleMatcher.ShouldReconcilePrimaryImage(
            BadgedUrl, BadgedUrl, UnbadgedUrl, hasSubtitle, enableBadges, out var target);

        Assert.True(changed);
        Assert.Equal(UnbadgedUrl, target);
    }

    [Theory]
    [InlineData("/media/poster.jpg")]
    [InlineData("https://example.com/v1/images/primary/JavDB/DE6Xa?badge=zimu.png")]
    [InlineData("http://metatube:8080/v1/images/primary/JavDB/OTHER?badge=zimu.png")]
    public void ShouldReconcilePrimaryImage_PreservesUnownedPoster(string currentImage)
    {
        var changed = SubtitleMatcher.ShouldReconcilePrimaryImage(
            currentImage, BadgedUrl, UnbadgedUrl, false, true, out var target);

        Assert.False(changed);
        Assert.Null(target);
    }
}
