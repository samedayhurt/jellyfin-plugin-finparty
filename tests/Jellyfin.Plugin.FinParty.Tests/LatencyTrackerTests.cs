using System.Collections.Generic;
using Jellyfin.Plugin.FinParty.Services;
using Xunit;

namespace Jellyfin.Plugin.FinParty.Tests;

/// <summary>
/// Tests for the rolling latency window.
/// </summary>
public class LatencyTrackerTests
{
    [Fact]
    public void Get_ReturnsEmptyForUnknownSession()
    {
        var tracker = new LatencyTracker();
        var stats = tracker.Get("nobody");

        Assert.Equal(0, stats.Samples);
        Assert.Equal("unknown", stats.Quality);
    }

    [Fact]
    public void Median_IgnoresASingleOutlier()
    {
        var tracker = new LatencyTracker();

        foreach (var sample in new long[] { 50, 52, 48, 51, 49, 3000 })
        {
            tracker.Record("s1", sample);
        }

        var stats = tracker.Get("s1");

        // The median must not be dragged upward by one bad sample; that is the
        // whole reason tuning keys off the median rather than the worst value.
        Assert.InRange(stats.MedianMs, 48, 60);
        Assert.Equal(3000, stats.WorstMs);
        Assert.Equal(6, stats.Samples);
    }

    [Fact]
    public void Jitter_SeparatesSteadyFromSwinging()
    {
        var steady = new LatencyTracker();
        var swinging = new LatencyTracker();

        for (var i = 0; i < 12; i++)
        {
            steady.Record("steady", 150);
            swinging.Record("swinging", i % 2 == 0 ? 40 : 400);
        }

        // Both links average out similarly, but only one of them is usable.
        Assert.True(steady.Get("steady").JitterMs < 10);
        Assert.True(swinging.Get("swinging").JitterMs > 100);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(120_000)]
    public void Record_RejectsImpossibleSamples(long sample)
    {
        var tracker = new LatencyTracker();
        tracker.Record("s1", sample);

        Assert.Equal(0, tracker.Get("s1").Samples);
    }

    [Fact]
    public void Window_IsBounded()
    {
        var tracker = new LatencyTracker();

        for (var i = 0; i < 500; i++)
        {
            tracker.Record("s1", 100);
        }

        Assert.True(tracker.Get("s1").Samples <= 24);
    }

    [Fact]
    public void Prune_DropsDeadSessions()
    {
        var tracker = new LatencyTracker();
        tracker.Record("alive", 100);
        tracker.Record("dead", 100);

        tracker.Prune(new HashSet<string> { "alive" });

        Assert.Equal(1, tracker.Get("alive").Samples > 0 ? 1 : 0);
        Assert.Equal(0, tracker.Get("dead").Samples);
    }

    [Theory]
    [InlineData(20, "excellent")]
    [InlineData(60, "good")]
    [InlineData(120, "fair")]
    [InlineData(400, "poor")]
    public void Quality_LabelsTheLink(long ping, string expected)
    {
        var tracker = new LatencyTracker();

        for (var i = 0; i < 5; i++)
        {
            tracker.Record("s1", ping);
        }

        Assert.Equal(expected, tracker.Get("s1").Quality);
    }
}
