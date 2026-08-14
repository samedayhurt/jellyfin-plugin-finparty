using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.FinParty.Services;

/// <summary>
/// A round-trip time summary for one session.
/// </summary>
/// <param name="SessionId">The session identifier.</param>
/// <param name="LatestMs">The most recent round-trip time in milliseconds.</param>
/// <param name="MedianMs">The median round-trip time across the retained window.</param>
/// <param name="JitterMs">Mean absolute deviation from the median, in milliseconds.</param>
/// <param name="WorstMs">The worst round-trip time in the retained window.</param>
/// <param name="Samples">How many samples the summary is based on.</param>
public readonly record struct LatencyStats(
    string SessionId,
    long LatestMs,
    long MedianMs,
    long JitterMs,
    long WorstMs,
    int Samples)
{
    /// <summary>
    /// Gets a plain-language quality label for the link.
    /// </summary>
    public string Quality => MedianMs switch
    {
        < 0 => "unknown",
        < 40 => "excellent",
        < 90 => "good",
        < 180 => "fair",
        _ => "poor"
    };
}

/// <summary>
/// Keeps a short rolling window of round-trip times per session.
/// </summary>
/// <remarks>
/// Jellyfin already measures round-trip time — clients send SyncPlay ping requests and the
/// server stores the result on each group member. It only ever uses the single worst value.
/// FinParty samples the same numbers over time so it can distinguish a link that is merely
/// slow (a steady 150 ms, which SyncPlay handles fine once tolerances are widened) from one
/// that is jittery (40 ms swinging to 400 ms, which is what actually breaks playback).
/// </remarks>
public sealed class LatencyTracker
{
    private const int WindowSize = 24;

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a round-trip time sample for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="pingMs">The observed round-trip time in milliseconds.</param>
    public void Record(string sessionId, long pingMs)
    {
        if (string.IsNullOrEmpty(sessionId) || pingMs <= 0 || pingMs > 60_000)
        {
            return;
        }

        _windows.GetOrAdd(sessionId, _ => new Window()).Add(pingMs);
    }

    /// <summary>
    /// Gets the statistics for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The statistics, or a zeroed value when the session is unknown.</returns>
    public LatencyStats Get(string sessionId)
    {
        if (!_windows.TryGetValue(sessionId, out var window))
        {
            return new LatencyStats(sessionId, -1, -1, -1, -1, 0);
        }

        return window.Summarise(sessionId);
    }

    /// <summary>
    /// Gets the statistics for several sessions.
    /// </summary>
    /// <param name="sessionIds">The session identifiers.</param>
    /// <returns>The statistics for each known session.</returns>
    public IReadOnlyList<LatencyStats> GetMany(IEnumerable<string> sessionIds)
        => sessionIds.Select(Get).Where(s => s.Samples > 0).ToList();

    /// <summary>
    /// Drops tracking state for sessions that no longer exist.
    /// </summary>
    /// <param name="liveSessionIds">The session identifiers still in use.</param>
    public void Prune(IReadOnlySet<string> liveSessionIds)
    {
        foreach (var key in _windows.Keys)
        {
            if (!liveSessionIds.Contains(key))
            {
                _windows.TryRemove(key, out _);
            }
        }
    }

    private sealed class Window
    {
        private readonly Queue<long> _samples = new(WindowSize);
        private readonly Lock _lock = new();

        public void Add(long value)
        {
            lock (_lock)
            {
                _samples.Enqueue(value);
                while (_samples.Count > WindowSize)
                {
                    _samples.Dequeue();
                }
            }
        }

        public LatencyStats Summarise(string sessionId)
        {
            long[] values;
            long latest;

            lock (_lock)
            {
                if (_samples.Count == 0)
                {
                    return new LatencyStats(sessionId, -1, -1, -1, -1, 0);
                }

                values = _samples.ToArray();
                latest = values[^1];
            }

            var sorted = (long[])values.Clone();
            Array.Sort(sorted);
            var median = sorted[sorted.Length / 2];

            long deviation = 0;
            foreach (var value in values)
            {
                deviation += Math.Abs(value - median);
            }

            return new LatencyStats(
                sessionId,
                latest,
                median,
                deviation / values.Length,
                sorted[^1],
                values.Length);
        }
    }
}
