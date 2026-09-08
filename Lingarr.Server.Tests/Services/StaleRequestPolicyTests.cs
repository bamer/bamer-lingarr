using System;
using Lingarr.Server.Services;
using Xunit;

namespace Lingarr.Server.Tests.Services;

public class StaleRequestPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
    private const int StaleHours = 12;

    [Theory]
    [InlineData(null)]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("Deleted")]
    [InlineData("Expired")]
    public void IsReleasable_StaleRequestWithDeadJob_ReturnsTrue(string? jobState)
    {
        var createdAt = Now.AddHours(-StaleHours - 1);

        Assert.True(StaleRequestPolicy.IsReleasable(createdAt, Now, StaleHours, jobState));
    }

    [Theory]
    [InlineData("Enqueued")]
    [InlineData("Scheduled")]
    [InlineData("Processing")]
    [InlineData("Awaiting")]
    public void IsReleasable_StaleRequestWithAliveJob_ReturnsFalse(string jobState)
    {
        // Regression: a request queued behind a deep backlog is older than the
        // threshold but its job is alive — releasing it would churn the queue.
        var createdAt = Now.AddHours(-StaleHours - 1);

        Assert.False(StaleRequestPolicy.IsReleasable(createdAt, Now, StaleHours, jobState));
    }

    [Fact]
    public void IsReleasable_RecentRequest_ReturnsFalseEvenWhenJobIsGone()
    {
        var createdAt = Now.AddMinutes(-5);

        Assert.False(StaleRequestPolicy.IsReleasable(createdAt, Now, StaleHours, null));
    }

    [Fact]
    public void IsReleasable_DisabledThreshold_ReturnsFalse()
    {
        var createdAt = Now.AddHours(-100);

        Assert.False(StaleRequestPolicy.IsReleasable(createdAt, Now, 0, null));
    }

    [Fact]
    public void IsReleasable_UnknownJobState_TreatedAsAlive()
    {
        // A storage hiccup must not free work that might still be queued.
        var createdAt = Now.AddHours(-StaleHours - 1);

        Assert.False(StaleRequestPolicy.IsReleasable(createdAt, Now, StaleHours, "Unknown"));
    }
}
