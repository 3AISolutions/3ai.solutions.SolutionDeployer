using SolutionDeployer.App.ViewModels;

namespace SolutionDeployer.App.Tests;

/// <summary>The "last deployed" text on a target row.</summary>
public sealed class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(20, "just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(3 * 3600 + 120, "3 h ago")]
    [InlineData(30 * 3600, "yesterday")]
    [InlineData(12 * 86400, "12 days ago")]
    public void Recent_deploys_read_as_how_long_ago(int secondsAgo, string expected)
    {
        Assert.Equal(expected, RelativeTime.Describe(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void Older_deploys_show_their_date()
    {
        var when = Now.AddDays(-90);

        Assert.Equal(when.ToLocalTime().ToString("yyyy-MM-dd"), RelativeTime.Describe(when, Now));
    }
}
