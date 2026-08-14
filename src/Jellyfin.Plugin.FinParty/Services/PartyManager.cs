using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.FinParty.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Controller.SyncPlay.Requests;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinParty.Services;

/// <summary>
/// Raised when the caller is not allowed to act on a device or party.
/// </summary>
public sealed class PartyForbiddenException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PartyForbiddenException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public PartyForbiddenException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Turns Jellyfin's session-scoped SyncPlay API into something a phone remote can drive.
/// </summary>
/// <remarks>
/// Jellyfin's own SyncPlay API only ever acts on the calling session, so a group can only be
/// assembled by every participant opting in from their own device. That is the step families
/// get stuck on. Because <see cref="ISyncPlayManager"/> accepts any <see cref="SessionInfo"/>,
/// a plugin can assemble the group on everyone's behalf.
/// <para>
/// The remote itself is deliberately never added to the group. A session that joins but never
/// reports itself ready would leave the group waiting forever, so the party is always hosted by
/// a real playback device and the remote acts through that device's session.
/// </para>
/// </remarks>
public sealed class PartyManager
{
    /// <summary>
    /// Characters that cannot be confused with each other when read aloud across a room.
    /// </summary>
    private const string CodeAlphabet = "ACDEFHJKLMNPQRTUVWXY3479";

    private readonly ISyncPlayManager _syncPlayManager;
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly SyncPlayReflector _reflector;
    private readonly LatencyTracker _latency;
    private readonly PartyTuner _tuner;
    private readonly ILogger<PartyManager> _logger;

    private readonly ConcurrentDictionary<string, PartyRecord> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, PartyRecord> _byGroup = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PartyManager"/> class.
    /// </summary>
    /// <param name="syncPlayManager">Jellyfin's SyncPlay manager.</param>
    /// <param name="sessionManager">Jellyfin's session manager.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="reflector">The SyncPlay internals accessor.</param>
    /// <param name="latency">The latency tracker.</param>
    /// <param name="tuner">The party tuner.</param>
    /// <param name="logger">The logger.</param>
    public PartyManager(
        ISyncPlayManager syncPlayManager,
        ISessionManager sessionManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        SyncPlayReflector reflector,
        LatencyTracker latency,
        PartyTuner tuner,
        ILogger<PartyManager> logger)
    {
        _syncPlayManager = syncPlayManager;
        _sessionManager = sessionManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _reflector = reflector;
        _latency = latency;
        _tuner = tuner;
        _logger = logger;
    }

    /// <summary>
    /// Lists the devices the caller may pull into a party.
    /// </summary>
    /// <param name="caller">The calling user.</param>
    /// <returns>The controllable devices.</returns>
    public IReadOnlyList<FinPartyDeviceDto> GetDevices(User caller)
    {
        Prune();

        var devices = new List<FinPartyDeviceDto>();
        var now = DateTime.UtcNow;

        foreach (var session in _sessionManager.Sessions)
        {
            if (session.UserId.Equals(default) || string.IsNullOrEmpty(session.Id))
            {
                continue;
            }

            // A device that cannot be told what to play is no use in a party.
            if (!session.SupportsMediaControl)
            {
                continue;
            }

            // The remote authenticates like any other client and therefore owns a session of its
            // own. It must never be offered as a party member: a session that joins but never
            // reports itself ready leaves the group waiting forever.
            if (IsRemoteSession(session))
            {
                continue;
            }

            if (!CanControl(caller, session))
            {
                continue;
            }

            var stats = _latency.Get(session.Id);
            var membership = FindMembership(session.Id);

            devices.Add(new FinPartyDeviceDto
            {
                SessionId = session.Id,
                DeviceName = string.IsNullOrWhiteSpace(session.DeviceName) ? "Unknown device" : session.DeviceName,
                Client = session.Client ?? string.Empty,
                UserName = session.UserName ?? string.Empty,
                UserId = session.UserId,
                IsMine = session.UserId.Equals(caller.Id),
                NowPlaying = session.NowPlayingItem?.Name,
                InParty = membership is not null,
                PartyCode = membership?.Code,
                LatencyMs = stats.Samples > 0 ? stats.MedianMs : -1,
                LinkQuality = stats.Samples > 0 ? stats.Quality : "unknown",
                SupportsSyncPlay = session.SupportsMediaControl,
                IdleSeconds = Math.Max(0, (now - session.LastActivityDate).TotalSeconds)
            });
        }

        return devices
            .OrderByDescending(d => d.IsMine)
            .ThenBy(d => d.IdleSeconds)
            .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Starts a new party hosted by one of the target devices.
    /// </summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="request">The create request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The party state and the outcome of each invitation.</returns>
    /// <exception cref="PartyForbiddenException">The caller may not control one of the devices.</exception>
    /// <exception cref="InvalidOperationException">No usable device was supplied.</exception>
    public (FinPartyStateDto State, FinPartyInviteResultDto Invites) CreateParty(
        User caller,
        FinPartyCreateRequest request,
        CancellationToken cancellationToken)
    {
        var targets = ResolveSessions(caller, request.SessionIds);

        if (targets.Count == 0)
        {
            throw new InvalidOperationException("Pick at least one device that is online and can play video.");
        }

        // The party must be hosted by a real playback device, never by the remote.
        var host = targets[0];
        var name = string.IsNullOrWhiteSpace(request.Name) ? DefaultPartyName(caller) : request.Name!.Trim();

        var group = _syncPlayManager.NewGroup(host, new NewGroupRequest(name), cancellationToken);

        var record = new PartyRecord(group.GroupId, NewCode(), name, caller.Id);
        record.Roster[host.Id] = 0;
        _byCode[record.Code] = record;
        _byGroup[record.GroupId] = record;

        _logger.LogInformation(
            "FinParty: {User} started party {Code} ({Name}) hosted by {Device}.",
            caller.Username,
            record.Code,
            name,
            host.DeviceName);

        var invites = new FinPartyInviteResultDto();
        invites.Joined.Add(host.Id);

        foreach (var session in targets.Skip(1))
        {
            JoinSession(record, session, invites, cancellationToken);
        }

        if (request.ItemId.HasValue && request.ItemId.Value != Guid.Empty)
        {
            Play(caller, record.GroupId, new FinPartyPlayRequest { ItemId = request.ItemId.Value }, cancellationToken);
        }

        return (BuildState(record), invites);
    }

    /// <summary>
    /// Adds devices to an existing party.
    /// </summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="groupId">The party group identifier.</param>
    /// <param name="sessionIds">The sessions to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome of each invitation.</returns>
    public FinPartyInviteResultDto Invite(
        User caller,
        Guid groupId,
        IReadOnlyList<string> sessionIds,
        CancellationToken cancellationToken)
    {
        var record = RequireParty(groupId);
        var targets = ResolveSessions(caller, sessionIds);
        var result = new FinPartyInviteResultDto();

        foreach (var session in targets)
        {
            JoinSession(record, session, result, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Removes a device from a party.
    /// </summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="groupId">The party group identifier.</param>
    /// <param name="sessionId">The session to remove.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public void Remove(User caller, Guid groupId, string sessionId, CancellationToken cancellationToken)
    {
        var record = RequireParty(groupId);
        var session = FindSession(sessionId) ?? throw new InvalidOperationException("That device is no longer online.");

        RequireControl(caller, session);

        _syncPlayManager.LeaveGroup(session, new LeaveGroupRequest(), cancellationToken);
        record.Roster.TryRemove(sessionId, out _);
        record.Touch();
    }

    /// <summary>
    /// Starts playback of an item across the whole party.
    /// </summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="groupId">The party group identifier.</param>
    /// <param name="request">The play request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public void Play(User caller, Guid groupId, FinPartyPlayRequest request, CancellationToken cancellationToken)
    {
        var record = RequireParty(groupId);
        var actor = RequireActor(caller, record);

        var startTicks = (long)Math.Max(0, request.StartSeconds) * TimeSpan.TicksPerSecond;
        var playRequest = new PlayGroupRequest(new[] { request.ItemId }, 0, startTicks);

        _syncPlayManager.HandleRequest(actor, playRequest, cancellationToken);
        record.Touch();

        _logger.LogInformation(
            "FinParty: {User} started playback in party {Code}.",
            caller.Username,
            record.Code);
    }

    /// <summary>
    /// Applies a transport command to the party.
    /// </summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="groupId">The party group identifier.</param>
    /// <param name="request">The playback request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public void Command(
        User caller,
        Guid groupId,
        IGroupPlaybackRequest request,
        CancellationToken cancellationToken)
    {
        var record = RequireParty(groupId);
        var actor = RequireActor(caller, record);

        _syncPlayManager.HandleRequest(actor, request, cancellationToken);
        record.Touch();
    }

    /// <summary>
    /// Gets the current state of a party.
    /// </summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="groupId">The party group identifier.</param>
    /// <returns>The party state.</returns>
    public FinPartyStateDto GetState(User caller, Guid groupId)
    {
        var record = RequireParty(groupId);
        return BuildState(record);
    }

    /// <summary>
    /// Resolves a short join code to a party.
    /// </summary>
    /// <param name="code">The join code.</param>
    /// <returns>The party group identifier, or <c>null</c> when the code is unknown.</returns>
    public Guid? ResolveCode(string code)
    {
        Prune();

        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return _byCode.TryGetValue(code.Trim(), out var record) ? record.GroupId : null;
    }

    /// <summary>
    /// Lists the parties currently running.
    /// </summary>
    /// <returns>The live parties.</returns>
    public IReadOnlyList<FinPartyStateDto> GetParties()
    {
        Prune();
        return _byGroup.Values.Select(BuildState).ToList();
    }

    /// <summary>
    /// Grants a user permission to use SyncPlay when the plugin is configured to do so.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>A task that completes when the permission has been persisted.</returns>
    public async Task EnsureSyncPlayAccessAsync(User user)
    {
        if (!Plugin.Config.AutoGrantSyncPlayAccess)
        {
            return;
        }

        if (user.SyncPlayAccess == SyncPlayUserAccessType.CreateAndJoinGroups)
        {
            return;
        }

        user.SyncPlayAccess = SyncPlayUserAccessType.CreateAndJoinGroups;
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

        _logger.LogInformation("FinParty granted SyncPlay access to {User}.", user.Username);
    }

    /// <summary>
    /// Grants SyncPlay access to the owners of the devices about to be pulled into a party.
    /// </summary>
    /// <remarks>
    /// Jellyfin enforces SyncPlay access in the HTTP layer, not in the manager, so joining a
    /// session server-side would otherwise quietly bypass it. Granting up front keeps the
    /// permission state honest instead — and means a family member who has never opened the
    /// setting still ends up in the party.
    /// </remarks>
    /// <param name="sessionIds">The sessions about to join.</param>
    /// <returns>A task that completes when every affected user has been updated.</returns>
    public async Task EnsureSyncPlayAccessForSessionsAsync(IReadOnlyList<string> sessionIds)
    {
        if (!Plugin.Config.AutoGrantSyncPlayAccess || sessionIds is null)
        {
            return;
        }

        var handled = new HashSet<Guid>();

        foreach (var sessionId in sessionIds)
        {
            var session = FindSession(sessionId);
            if (session is null || !handled.Add(session.UserId))
            {
                continue;
            }

            var user = _userManager.GetUserById(session.UserId);
            if (user is not null)
            {
                await EnsureSyncPlayAccessAsync(user).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Determines whether the caller may act on a session.
    /// </summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="session">The target session.</param>
    /// <returns><c>true</c> when the caller may control the session.</returns>
    public static bool CanControl(User caller, SessionInfo session)
    {
        if (HasPermission(caller, PermissionKind.IsAdministrator))
        {
            return true;
        }

        if (session.UserId.Equals(caller.Id))
        {
            return true;
        }

        if (!Plugin.Config.AllowGuestsToInviteDevices)
        {
            return false;
        }

        return HasPermission(caller, PermissionKind.EnableRemoteControlOfOtherUsers);
    }

    /// <summary>
    /// Reads a permission straight off the user entity.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="kind">The permission to read.</param>
    /// <returns><c>true</c> when the permission is granted.</returns>
    public static bool HasPermission(User user, PermissionKind kind)
    {
        foreach (var permission in user.Permissions)
        {
            if (permission.Kind == kind)
            {
                return permission.Value;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether a session is a FinParty remote rather than a playback device.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns><c>true</c> when the session is a remote.</returns>
    public static bool IsRemoteSession(SessionInfo session)
        => session.Client is not null
           && session.Client.StartsWith("FinParty", StringComparison.OrdinalIgnoreCase);

    private static string DefaultPartyName(User caller)
        => string.Create(CultureInfo.InvariantCulture, $"{caller.Username}'s watch party");

    private void JoinSession(
        PartyRecord record,
        SessionInfo session,
        FinPartyInviteResultDto result,
        CancellationToken cancellationToken)
    {
        try
        {
            _syncPlayManager.JoinGroup(session, new JoinGroupRequest(record.GroupId), cancellationToken);
            record.Roster[session.Id] = 0;
            record.Touch();
            result.Joined.Add(session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "FinParty could not add {Device} to party {Code}.",
                session.DeviceName,
                record.Code);

            result.Failed[session.Id] = ex.Message;
        }
    }

    private IReadOnlyList<SessionInfo> ResolveSessions(User caller, IReadOnlyList<string> sessionIds)
    {
        var resolved = new List<SessionInfo>();

        foreach (var sessionId in sessionIds ?? Array.Empty<string>())
        {
            var session = FindSession(sessionId);
            if (session is null)
            {
                continue;
            }

            RequireControl(caller, session);
            resolved.Add(session);
        }

        return resolved;
    }

    private void RequireControl(User caller, SessionInfo session)
    {
        if (!CanControl(caller, session))
        {
            throw new PartyForbiddenException(
                $"You do not have permission to control {session.DeviceName}.");
        }
    }

    private SessionInfo? FindSession(string sessionId)
        => string.IsNullOrEmpty(sessionId)
            ? null
            : _sessionManager.Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));

    /// <summary>
    /// Picks a session inside the party to issue a request as. SyncPlay attributes every request
    /// to a member, so the remote borrows a real member's session.
    /// </summary>
    /// <param name="caller">The calling user.</param>
    /// <param name="record">The party.</param>
    /// <returns>The session to act as.</returns>
    private SessionInfo RequireActor(User caller, PartyRecord record)
    {
        var members = record.Roster.Keys
            .Select(FindSession)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

        if (members.Count == 0)
        {
            throw new InvalidOperationException("Nobody is left in this party.");
        }

        // Prefer a device the caller is definitely allowed to drive.
        var preferred = members.FirstOrDefault(s => CanControl(caller, s));

        if (preferred is null)
        {
            throw new PartyForbiddenException("You do not have permission to control this party.");
        }

        return preferred;
    }

    private PartyRecord RequireParty(Guid groupId)
    {
        Prune();

        if (!_byGroup.TryGetValue(groupId, out var record))
        {
            throw new InvalidOperationException("That party has ended.");
        }

        return record;
    }

    private PartyRecord? FindMembership(string sessionId)
        => _byGroup.Values.FirstOrDefault(r => r.Roster.ContainsKey(sessionId));

    private FinPartyStateDto BuildState(PartyRecord record)
    {
        var state = new FinPartyStateDto
        {
            GroupId = record.GroupId,
            Code = record.Code,
            Name = record.Name
        };

        var group = _reflector.GetGroup(record.GroupId);
        var members = new List<FinPartyMemberDto>();

        if (group is not null)
        {
            state.PositionSeconds = TimeSpan.FromTicks(Math.Max(0, group.PositionTicks)).TotalSeconds;

            var playingItemId = group.PlayQueue?.GetPlayingItemId();
            if (playingItemId.HasValue && playingItemId.Value != Guid.Empty)
            {
                state.NowPlayingItemId = playingItemId.Value;
                var item = _libraryManager.GetItemById(playingItemId.Value);
                if (item is not null)
                {
                    state.NowPlaying = item.Name;
                    state.RuntimeSeconds = TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds;
                }
            }

            var participants = _reflector.GetParticipants(group);

            // Keep our roster honest: clients can leave a group without going through us.
            if (participants.Count > 0)
            {
                foreach (var stale in record.Roster.Keys.Where(id => !participants.ContainsKey(id)).ToList())
                {
                    record.Roster.TryRemove(stale, out _);
                }
            }

            foreach (var member in participants.Values)
            {
                record.Roster[member.SessionId] = 0;

                var stats = _latency.Get(member.SessionId);
                var session = FindSession(member.SessionId);

                members.Add(new FinPartyMemberDto
                {
                    SessionId = member.SessionId,
                    UserName = member.UserName ?? string.Empty,
                    DeviceName = session?.DeviceName ?? string.Empty,
                    IsBuffering = member.IsBuffering,
                    Released = member.IgnoreGroupWait,
                    LatencyMs = stats.Samples > 0 ? stats.MedianMs : -1,
                    LinkQuality = stats.Samples > 0 ? stats.Quality : "unknown"
                });
            }

            state.AnyoneBuffering = group.IsBuffering();
        }
        else
        {
            // Reflection is unavailable: fall back to our own roster.
            foreach (var sessionId in record.Roster.Keys)
            {
                var session = FindSession(sessionId);
                if (session is null)
                {
                    continue;
                }

                var stats = _latency.Get(sessionId);
                members.Add(new FinPartyMemberDto
                {
                    SessionId = sessionId,
                    UserName = session.UserName ?? string.Empty,
                    DeviceName = session.DeviceName ?? string.Empty,
                    LatencyMs = stats.Samples > 0 ? stats.MedianMs : -1,
                    LinkQuality = stats.Samples > 0 ? stats.Quality : "unknown"
                });
            }
        }

        state.Members = members;
        state.State = ResolveGroupState(record.GroupId);

        var snapshot = _tuner.GetSnapshot(record.GroupId);
        if (snapshot.HasValue)
        {
            var value = snapshot.Value;
            state.Tuning = new FinPartyTuningDto
            {
                Mode = value.Mode,
                MaxPlaybackOffsetMs = value.MaxPlaybackOffsetMs,
                TimeSyncOffsetMs = value.TimeSyncOffsetMs,
                ObservedRttMs = value.ObservedRttMs,
                ObservedJitterMs = value.ObservedJitterMs,
                Explanation = Explain(value)
            };
        }

        return state;
    }

    private static string Explain(TuningSnapshot snapshot)
    {
        if (snapshot.ObservedRttMs <= 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Using configured tolerances ({snapshot.MaxPlaybackOffsetMs} ms) until round-trip times are measured.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Worst link is {snapshot.ObservedRttMs} ms with {snapshot.ObservedJitterMs} ms jitter, " +
            $"so playback drift is tolerated up to {snapshot.MaxPlaybackOffsetMs} ms " +
            $"instead of Jellyfin's fixed 500 ms.");
    }

    private string ResolveGroupState(Guid groupId)
    {
        // GroupInfoDto carries the state, but ListGroups needs a session to scope visibility.
        var anySession = _byGroup.TryGetValue(groupId, out var record)
            ? record.Roster.Keys.Select(FindSession).FirstOrDefault(s => s is not null)
            : null;

        if (anySession is null)
        {
            return "Idle";
        }

        try
        {
            var info = _syncPlayManager.GetGroup(anySession, groupId);
            return info?.State.ToString() ?? "Idle";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FinParty could not read state for group {GroupId}.", groupId);
            return "Idle";
        }
    }

    private void Prune()
    {
        var lifetime = TimeSpan.FromMinutes(Math.Max(5, Plugin.Config.PartyCodeLifetimeMinutes));
        var now = DateTime.UtcNow;
        var liveGroups = _reflector.IsAvailable
            ? _reflector.GetGroups().Select(g => g.GroupId).ToHashSet()
            : null;

        foreach (var record in _byGroup.Values.ToList())
        {
            var expired = now - record.LastTouchedUtc > lifetime;
            var gone = liveGroups is not null && !liveGroups.Contains(record.GroupId);

            if (!expired && !gone)
            {
                continue;
            }

            _byGroup.TryRemove(record.GroupId, out _);
            _byCode.TryRemove(record.Code, out _);

            _logger.LogInformation(
                "FinParty cleaned up party {Code} ({Reason}).",
                record.Code,
                gone ? "group ended" : "idle timeout");
        }
    }

    private string NewCode()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var code = string.Create(4, 0, static (span, _) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
                }
            });

            if (!_byCode.ContainsKey(code))
            {
                return code;
            }
        }

        // Astronomically unlikely; fall back to something guaranteed unique.
        return Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    }

    private sealed class PartyRecord
    {
        public PartyRecord(Guid groupId, string code, string name, Guid hostUserId)
        {
            GroupId = groupId;
            Code = code;
            Name = name;
            HostUserId = hostUserId;
            CreatedUtc = DateTime.UtcNow;
            LastTouchedUtc = CreatedUtc;
        }

        public Guid GroupId { get; }

        public string Code { get; }

        public string Name { get; }

        public Guid HostUserId { get; }

        public DateTime CreatedUtc { get; }

        public DateTime LastTouchedUtc { get; private set; }

        public ConcurrentDictionary<string, byte> Roster { get; } = new(StringComparer.Ordinal);

        public void Touch() => LastTouchedUtc = DateTime.UtcNow;
    }
}
