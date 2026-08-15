using System.Collections.Generic;
using Jellyfin.Plugin.FinParty.Services;
using Xunit;

namespace Jellyfin.Plugin.FinParty.Tests;

/// <summary>
/// Documents the record-on-change rule that keeps the seed ping out of the measured window.
/// </summary>
/// <remarks>
/// The tuner does the change-detection (it holds the last-seen value), but the invariant it
/// protects lives here: a value that never moves must not produce jitter. Live testing showed a
/// Moonfin device — which never sends a real ping — being flagged as an "unstable link" purely
/// from the seed leaking into the window. This pins the tracker's behaviour once real,
/// already-differing samples reach it.
/// </remarks>
public class LatencySeedTests
{
    [Fact]
    public void ASingleRepeatedValueHasNoJitter()
    {
        var tracker = new LatencyTracker();

        // What the tuner records for a steady client: one real sample.
        tracker.Record("s", 30);

        var stats = tracker.Get("s");
        Assert.Equal(0, stats.JitterMs);
        Assert.Equal(30, stats.MedianMs);
    }

    [Fact]
    public void RealVaryingSamplesStillProduceJitter()
    {
        var tracker = new LatencyTracker();
        foreach (var sample in new long[] { 40, 400, 40, 400, 40, 400 })
        {
            tracker.Record("s", sample);
        }

        // A genuinely unstable link must still be caught.
        Assert.True(tracker.Get("s").JitterMs > 100);
    }

    [Fact]
    public void AnUnsampledSessionIsUnknownNotZero()
    {
        var tracker = new LatencyTracker();

        // A device the tuner never recorded (e.g. one that only ever carried the seed) reports
        // no measurement rather than a fabricated one.
        var stats = tracker.Get("never-pinged");
        Assert.Equal(0, stats.Samples);
        Assert.Equal("unknown", stats.Quality);
        Assert.Empty(tracker.GetMany(new[] { "never-pinged" }));
    }
}
