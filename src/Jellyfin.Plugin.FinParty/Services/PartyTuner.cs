using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FinParty.Configuration;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinParty.Services;

/// <summary>
/// The tolerances FinParty most recently applied to a group.
/// </summary>
/// <param name="GroupId">The group identifier.</param>
/// <param name="Mode">The tuning mode that produced these values.</param>
/// <param name="DefaultPingMs">The applied default ping.</param>
/// <param name="TimeSyncOffsetMs">The applied clock-skew tolerance.</param>
/// <param name="MaxPlaybackOffsetMs">The applied playback drift tolerance.</param>
/// <param name="ObservedRttMs">The worst median round-trip time observed in the group.</param>
/// <param name="ObservedJitterMs">The worst jitter observed in the group.</param>
/// <param name="AppliedUtc">When the values were applied.</param>
public readonly record struct TuningSnapshot(
    Guid GroupId,
    string Mode,
    long DefaultPingMs,
    long TimeSyncOffsetMs,
    long MaxPlaybackOffsetMs,
    long ObservedRttMs,
    long ObservedJitterMs,
    DateTime AppliedUtc);

/// <summary>
/// Continuously retunes live SyncPlay groups to the network they are actually running on,
/// and stops one stuck member from holding the whole party hostage.
/// </summary>
public sealed class PartyTuner : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private readonly SyncPlayReflector _reflector;
    private readonly LatencyTracker _latency;
    private readonly ISyncPlayManager _syncPlayManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<PartyTuner> _logger;

    private readonly ConcurrentDictionary<Guid, TuningSnapshot> _applied = new();
    private readonly ConcurrentDictionary<string, DateTime> _bufferingSince = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _released = new(StringComparer.Ordinal);

    // Last observed raw ping per session, used to record only genuine changes and skip the seed.
    private readonly ConcurrentDictionary<string, long> _lastPing = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="PartyTuner"/> class.
    /// </summary>
    /// <param name="reflector">The SyncPlay internals accessor.</param>
    /// <param name="latency">The latency tracker.</param>
    /// <param name="syncPlayManager">Jellyfin's SyncPlay manager.</param>
    /// <param name="sessionManager">Jellyfin's session manager.</param>
    /// <param name="logger">The logger.</param>
    public PartyTuner(
        SyncPlayReflector reflector,
        LatencyTracker latency,
        ISyncPlayManager syncPlayManager,
        ISessionManager sessionManager,
        ILogger<PartyTuner> logger)
    {
        _reflector = reflector;
        _latency = latency;
        _syncPlayManager = syncPlayManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the tolerances currently applied to each live group.
    /// </summary>
    public IReadOnlyDictionary<Guid, TuningSnapshot> Snapshots => _applied;

    /// <summary>
    /// Gets the tolerances applied to a single group.
    /// </summary>
    /// <param name="groupId">The group identifier.</param>
    /// <returns>The snapshot, or <c>null</c> when the group has not been tuned.</returns>
    public TuningSnapshot? GetSnapshot(Guid groupId)
        => _applied.TryGetValue(groupId, out var snapshot) ? snapshot : null;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FinParty tuner started. SyncPlay internals: {Health}", _reflector.HealthSummary);

        using var timer = new PeriodicTimer(TickInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                Tick(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A tuner fault must never take SyncPlay down with it.
                _logger.LogError(ex, "FinParty tuner tick failed.");
            }
        }
    }

    private void Tick(CancellationToken cancellationToken)
    {
        var config = Plugin.Config;
        var groups = _reflector.GetGroups();

        if (groups.Count == 0)
        {
            if (!_applied.IsEmpty)
            {
                _applied.Clear();
                _bufferingSince.Clear();
                _released.Clear();
                _lastPing.Clear();
            }

            return;
        }

        var sessions = BuildSessionIndex();
        var liveGroupIds = new HashSet<Guid>();

        foreach (var group in groups)
        {
            liveGroupIds.Add(group.GroupId);

            var members = _reflector.GetParticipants(group);
            SampleLatency(members);

            if (config.Tuning != TuningMode.Off)
            {
                ApplyTuning(group, members, config);
            }

            if (config.EnableStallBreaker)
            {
                BreakStalls(group, members, sessions, config, cancellationToken);
            }
        }

        foreach (var staleGroupId in _applied.Keys.Where(id => !liveGroupIds.Contains(id)).ToList())
        {
            _applied.TryRemove(staleGroupId, out _);
        }

        var live = sessions.Keys.ToHashSet(StringComparer.Ordinal);
        _latency.Prune(live);
        foreach (var dead in _lastPing.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _lastPing.TryRemove(dead, out _);
        }
    }

    private Dictionary<string, SessionInfo> BuildSessionIndex()
    {
        var index = new Dictionary<string, SessionInfo>(StringComparer.Ordinal);

        foreach (var session in _sessionManager.Sessions)
        {
            if (!string.IsNullOrEmpty(session.Id))
            {
                index[session.Id] = session;
            }
        }

        return index;
    }

    private void SampleLatency(IReadOnlyDictionary<string, GroupMember> members)
    {
        foreach (var member in members.Values)
        {
            if (member.Ping <= 0)
            {
                continue;
            }

            // Jellyfin seeds GroupMember.Ping once when the session joins and only ever changes
            // it when a real client ping arrives. So record on *change*, not on "differs from the
            // current DefaultPing" — the old test wrongly recorded the static seed once FinParty
            // had retuned DefaultPing, manufacturing jitter for clients that never ping at all
            // (e.g. Moonfin). A member whose value never moves contributes nothing, which is the
            // honest answer: we have no measurement, so tuning falls back to the configured floor.
            var previous = _lastPing.TryGetValue(member.SessionId, out var last) ? (long?)last : null;
            _lastPing[member.SessionId] = member.Ping;

            if (previous.HasValue && previous.Value != member.Ping)
            {
                _latency.Record(member.SessionId, member.Ping);
            }
        }
    }

    private void ApplyTuning(
        IGroupStateContext group,
        IReadOnlyDictionary<string, GroupMember> members,
        PluginConfiguration config)
    {
        long worstRtt = 0;
        long worstJitter = 0;

        foreach (var sessionId in members.Keys)
        {
            var stats = _latency.Get(sessionId);
            if (stats.Samples > 0)
            {
                worstRtt = Math.Max(worstRtt, stats.MedianMs);
                worstJitter = Math.Max(worstJitter, stats.JitterMs);
            }
        }

        long defaultPing;
        long timeSyncOffset;
        long maxPlaybackOffset;

        if (config.Tuning == TuningMode.Adaptive && worstRtt > 0)
        {
            // Playback tolerance has to cover the round trip plus the swing around it, or the
            // server force-seeks a client that was never actually out of position.
            var derived = (long)(worstRtt * config.AdaptiveRttMultiplier) + (worstJitter * 2);
            maxPlaybackOffset = Clamp(derived, config.MaxPlaybackOffsetMs, config.AdaptiveCeilingMs);

            // Clock-skew tolerance has to be looser still, because it gates whether the client's
            // timestamp is trusted at all. Losing that makes every correction worse, not better.
            timeSyncOffset = Clamp(
                Math.Max(config.TimeSyncOffsetMs, (worstRtt * 6) + (worstJitter * 4)),
                config.TimeSyncOffsetMs,
                config.AdaptiveCeilingMs * 2);

            defaultPing = Clamp(worstRtt, 50, config.AdaptiveCeilingMs);
        }
        else
        {
            defaultPing = config.DefaultPingMs;
            timeSyncOffset = config.TimeSyncOffsetMs;
            maxPlaybackOffset = config.MaxPlaybackOffsetMs;
        }

        var alreadyCorrect = group.DefaultPing == defaultPing
                             && group.TimeSyncOffset == timeSyncOffset
                             && group.MaxPlaybackOffset == maxPlaybackOffset;

        if (alreadyCorrect)
        {
            return;
        }

        if (!_reflector.ApplyTuning(group, defaultPing, timeSyncOffset, maxPlaybackOffset))
        {
            return;
        }

        var snapshot = new TuningSnapshot(
            group.GroupId,
            config.Tuning.ToString(),
            defaultPing,
            timeSyncOffset,
            maxPlaybackOffset,
            worstRtt,
            worstJitter,
            DateTime.UtcNow);

        var previous = _applied.TryGetValue(group.GroupId, out var old) ? old : default;
        _applied[group.GroupId] = snapshot;

        // Only narrate meaningful movement, otherwise this logs every couple of seconds.
        if (Math.Abs(previous.MaxPlaybackOffsetMs - maxPlaybackOffset) >= 250)
        {
            _logger.LogInformation(
                "FinParty retuned group {GroupId}: worst RTT {Rtt} ms (jitter {Jitter} ms) -> " +
                "playback tolerance {Offset} ms, clock tolerance {TimeSync} ms.",
                group.GroupId.ToString(),
                worstRtt,
                worstJitter,
                maxPlaybackOffset,
                timeSyncOffset);
        }
    }

    private void BreakStalls(
        IGroupStateContext group,
        IReadOnlyDictionary<string, GroupMember> members,
        IReadOnlyDictionary<string, SessionInfo> sessions,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var threshold = TimeSpan.FromSeconds(Math.Max(5, config.StallBreakerSeconds));

        foreach (var member in members.Values)
        {
            var key = string.Create(
                CultureInfo.InvariantCulture,
                $"{group.GroupId:N}:{member.SessionId}");

            if (!member.IsBuffering)
            {
                _bufferingSince.TryRemove(key, out _);

                // The member recovered. Put them back under the group's protection so the
                // next genuine buffer event is waited on again.
                if (_released.TryRemove(key, out _)
                    && sessions.TryGetValue(member.SessionId, out var recovered))
                {
                    SendIgnoreWait(recovered, false, cancellationToken);
                    _logger.LogInformation(
                        "FinParty: {User} recovered and rejoined the wait in group {GroupId}.",
                        member.UserName,
                        group.GroupId.ToString());
                }

                continue;
            }

            if (member.IgnoreGroupWait || _released.ContainsKey(key))
            {
                continue;
            }

            var since = _bufferingSince.GetOrAdd(key, now);
            if (now - since < threshold)
            {
                continue;
            }

            if (!sessions.TryGetValue(member.SessionId, out var session))
            {
                continue;
            }

            // Ask SyncPlay to stop waiting for this member. This is the same request the
            // official clients send from their "ignore me" control, so the group resumes
            // through the normal state machine.
            if (SendIgnoreWait(session, true, cancellationToken))
            {
                _released[key] = 0;
                _bufferingSince.TryRemove(key, out _);

                _logger.LogWarning(
                    "FinParty released group {GroupId}: {User} has been buffering for {Seconds:F0}s. " +
                    "The rest of the party continues; they will rejoin automatically once they catch up.",
                    group.GroupId.ToString(),
                    member.UserName,
                    (now - since).TotalSeconds);
            }
        }
    }

    private bool SendIgnoreWait(SessionInfo session, bool ignoreWait, CancellationToken cancellationToken)
    {
        try
        {
            _syncPlayManager.HandleRequest(session, new IgnoreWaitGroupRequest(ignoreWait), cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FinParty could not send IgnoreWait for session {SessionId}.", session.Id);
            return false;
        }
    }

    private static long Clamp(long value, long min, long max)
        => max < min ? min : Math.Clamp(value, min, max);
}
