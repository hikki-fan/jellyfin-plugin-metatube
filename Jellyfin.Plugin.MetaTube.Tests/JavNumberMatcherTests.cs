using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

public sealed class JavNumberMatcherTests
{
    [Fact]
    public void ExactMatchesFirst_PromotesExactNumberAndPreservesRemainingOrder()
    {
        var results = new[] { "MGMJ-070", "MGMJ-071", "MGMJ-007", "MGMJ-072" };

        var ordered = JavNumberMatcher.ExactMatchesFirst(results, "mgmj_007", value => value);

        Assert.Equal(new[] { "MGMJ-007", "MGMJ-070", "MGMJ-071", "MGMJ-072" }, ordered);
    }

    [Theory]
    [InlineData("ordinary movie title")]
    [InlineData("MGMJ-007.mp4")]
    [InlineData("")]
    public void ExactMatchesFirst_LeavesNonIdentifierQueriesUnchanged(string query)
    {
        var results = new[] { "MGMJ-070", "MGMJ-007" };

        var ordered = JavNumberMatcher.ExactMatchesFirst(results, query, value => value);

        Assert.Equal(results, ordered);
    }

    [Theory]
    [InlineData("BrazzersExxtras.24.01.15")]
    [InlineData("BrazzersExxtras.24.01.15-C")]
    [InlineData("BrazzersExxtras.24.01.15-UC")]
    [InlineData("BrazzersExxtras.24.01.15_c")]
    [InlineData("BrazzersExxtras.24.01.15_uc")]
    [InlineData("brazzersexxtras.24.01.15")]
    [InlineData("Twistys.23.11.05")]
    [InlineData("Tushy.22.03.14")]
    [InlineData("Sweet-Sinner.24.01.15")]
    [InlineData("21Sextury.20.05.10")]
    [InlineData("BrazzersExxtras.24.01.15.mp4")]
    [InlineData("BrazzersExxtras.24.01.15-C.mp4.strm")]
    public void IsWesternSceneNumber_ValidIdentifiers_ReturnsTrue(string number)
    {
        Assert.True(JavNumberMatcher.IsWesternSceneNumber(number));
    }

    [Theory]
    [InlineData("MGMJ-070")]
    [InlineData("GANA-1928")]
    [InlineData("FC2-123456")]
    [InlineData("FC2-PPV-123456")]
    [InlineData("ABP-030")]
    [InlineData("ABP-030-C")]
    [InlineData("ordinary movie title")]
    [InlineData("MGMJ-007.mp4")]
    [InlineData("192.168.50.100")]
    [InlineData("Studio.24.13.15")]
    [InlineData("Studio.24.01.32")]
    [InlineData("")]
    [InlineData(null)]
    public void IsWesternSceneNumber_NonWesternOrMediaExtension_ReturnsFalse(string number)
    {
        Assert.False(JavNumberMatcher.IsWesternSceneNumber(number));
    }

    [Theory]
    [InlineData("BrazzersExxtras.24.01.15-C", "BrazzersExxtras.24.01.15", true)]
    [InlineData("BrazzersExxtras.24.01.15-UC", "BrazzersExxtras.24.01.15", true)]
    [InlineData("brazzersexxtras.24.01.15", "BrazzersExxtras.24.01.15", true)]
    [InlineData("BrazzersExxtras.24.01.15", "BrazzersExxtras.24.01.16", false)]
    [InlineData("GANA-1928", "GANA-1928", true)]
    [InlineData("MGMJ-070", "mgmj_070", true)]
    [InlineData("MGMJ-070", "MGMJ-007", false)]
    [InlineData("FC2-123456", "FC2-123456", true)]
    [InlineData("BrazzersExxtras.24.01.15-C.mp4.strm", "BrazzersExxtras.24.01.15", true)]
    public void IsExactMatch_ValidatesMatches(string query, string number, bool expected)
    {
        Assert.Equal(expected, JavNumberMatcher.IsExactMatch(query, number));
    }

    [Fact]
    public void ExactMatchesFirst_PromotesExactWesternSceneNumber()
    {
        var results = new[] { "OtherStudio.24.01.15", "BrazzersExxtras.24.01.15", "AnotherStudio.24.01.15" };

        var ordered = JavNumberMatcher.ExactMatchesFirst(
            results, "BrazzersExxtras.24.01.15-C", value => value);

        Assert.Equal(new[] { "BrazzersExxtras.24.01.15", "OtherStudio.24.01.15", "AnotherStudio.24.01.15" }, ordered);
    }

    [Fact]
    public void SelectMetadataResult_WesternScene_ExactMatchPresent_ReturnsExactMatch()
    {
        var results = new[]
        {
            new { Provider = "JavDB", Number = "OtherStudio.24.01.15" },
            new { Provider = "JavDB", Number = "BrazzersExxtras.24.01.15" },
            new { Provider = "JavDB", Number = "ThirdStudio.24.01.15" }
        };

        var selected = JavNumberMatcher.SelectMetadataResult(
            "BrazzersExxtras.24.01.15-UC", results, r => r.Number);

        Assert.NotNull(selected);
        Assert.Equal("BrazzersExxtras.24.01.15", selected.Number);
    }

    [Fact]
    public void SelectMetadataResult_WesternScene_ExactMatchAbsent_ReturnsNull()
    {
        var results = new[]
        {
            new { Provider = "JavDB", Number = "OtherStudio.24.01.15" },
            new { Provider = "JavDB", Number = "DifferentStudio.24.01.15" }
        };

        var selected = JavNumberMatcher.SelectMetadataResult(
            "BrazzersExxtras.24.01.15", results, r => r.Number);

        Assert.Null(selected);
    }

    [Fact]
    public void SelectMetadataResult_NonWestern_ExactMatchAbsent_PreservesFirstFuzzyResult()
    {
        var results = new[]
        {
            new { Provider = "JavDB", Number = "MGMJ-007" },
            new { Provider = "JavDB", Number = "MGMJ-071" }
        };

        // For non-Western query MGMJ-070 without exact match, JAV behavior preserves first fuzzy result
        var selected = JavNumberMatcher.SelectMetadataResult(
            "MGMJ-070", results, r => r.Number);

        Assert.NotNull(selected);
        Assert.Equal("MGMJ-007", selected.Number);
    }
}
