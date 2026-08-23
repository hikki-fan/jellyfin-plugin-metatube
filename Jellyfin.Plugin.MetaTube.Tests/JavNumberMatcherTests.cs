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
}
