# Why Jellyfin SyncPlay falls apart over a VPN

Notes from taking Jellyfin 10.11.11's SyncPlay implementation apart while building FinParty.
Everything here is cited to source at tag [`v10.11.11`](https://github.com/jellyfin/jellyfin/tree/v10.11.11).

---

## Summary

SyncPlay is not broken over Tailscale or WireGuard because tunnels are slow. It breaks because
three timing tolerances are **compile-time constants sized for a LAN**, and one of them is
compared against a value in the wrong unit.

| Constant | Value | Meaning |
| --- | --- | --- |
| `DefaultPing` | 500 ms | Assumed round-trip time before a client reports one |
| `TimeSyncOffset` | 2000 ms | Skew beyond which a client's own timestamp is discarded |
| `MaxPlaybackOffset` | 500 ms | Drift beyond which the server force-seeks the client |

All three are declared on `Group` as get-only auto-properties with inline initialisers
([`Group.cs:91-103`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/SyncPlay/Group.cs#L91-L103)):

```csharp
public long DefaultPing { get; } = 500;
public long TimeSyncOffset { get; } = 2000;
public long MaxPlaybackOffset { get; } = 500;
```

There is no configuration surface for any of them — not in the server config, not per group,
not per user. `IGroupStateContext` exposes them as read-only
([`IGroupStateContext.cs:22-34`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/SyncPlay/IGroupStateContext.cs#L22-L34)).

---

## Finding 1: a unit mismatch disables the recovery grace period

**Location:** [`WaitingGroupState.cs:499-511`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L499-L511)

When a client that was buffering resumes playback but does not tell the group in time, the
server computes how long the rest of the group should be given to recover:

```csharp
delayTicks = context.GetHighestPing() * 2 * TimeSpan.TicksPerMillisecond;  // ticks
delayTicks = Math.Max(delayTicks, context.DefaultPing);                    // 500 — but ticks?
```

The first line produces **ticks**. The second line floors it against `DefaultPing`, which is
**500 milliseconds**. As a tick count, 500 ticks is 0.05 ms.

`GetHighestPing() * 2 * TicksPerMillisecond` is smaller than 500 ticks only when the highest
ping is under 0.025 ms, which never happens. **The floor therefore never applies**, and the
intended "give the group at least `DefaultPing` to recover" guarantee is silently absent.

For the line to do what the surrounding comment says it does, it would need to be:

```csharp
delayTicks = Math.Max(delayTicks, context.DefaultPing * TimeSpan.TicksPerMillisecond);
```

The practical effect is worst on a low-latency link, where the computed delay collapses to
near zero and the group is told to unpause essentially immediately — before the recovering
client has actually caught up. On a high-latency link the `GetHighestPing() * 2` term is large
enough to mask the bug.

*Status: not yet reported upstream. See the issue template in this repo.*

---

## Finding 2: 500 ms of drift tolerance is below the noise floor of a relayed link

**Location:** [`WaitingGroupState.cs:441-466`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L441-L466)

```csharp
var clientPosition = TimeSpan.FromTicks(requestTicks) + elapsedTime;
var delayTicks = context.PositionTicks - clientPosition.Ticks;
var maxPlaybackOffsetTicks = TimeSpan.FromMilliseconds(context.MaxPlaybackOffset).Ticks;
...
if (!request.IsPlaying && Math.Abs(delayTicks) > maxPlaybackOffsetTicks)
{
    context.SetBuffering(session, true);
    var command = context.NewSyncPlayCommand(SendCommandType.Seek);   // force-seek
    ...
    _logger.LogWarning("Session {SessionId} got lost in time, correcting.", session.Id);
}
```

`clientPosition` is derived from a timestamp the client sent, which arrived over the network.
The measurement error in that value is on the order of the round-trip time.

A Tailscale connection that has fallen back to a DERP relay typically runs 80–300 ms with
significant jitter. The error term is then a large fraction of — or larger than — the 500 ms
threshold. The server concludes the client is out of position, force-seeks it, which causes a
real buffer, which produces a genuinely late position report, which triggers another correction.

**This is the "everyone keeps jumping around" symptom.** It is a feedback loop created by
measuring with a ruler shorter than the error bars.

`Ping` itself is only ever consumed as `GetHighestPing()`
([`Group.cs:445-453`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/SyncPlay/Group.cs#L445-L453)) — a single worst-case
number. Nothing in Jellyfin distinguishes a steadily slow link (fine, once tolerances match)
from a jittery one (the actual problem).

---

## Finding 3: discarding the client timestamp makes corrections worse

**Location:** [`WaitingGroupState.cs:425-433`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L425-L433)

```csharp
var elapsedTime = currentTime.Subtract(request.When);
var timeSyncThresholdTicks = TimeSpan.FromMilliseconds(context.TimeSyncOffset).Ticks;
if (Math.Abs(elapsedTime.Ticks) > timeSyncThresholdTicks)
{
    _logger.LogWarning("Session {SessionId} is not time syncing properly. Ignoring elapsed time.", session.Id);
    elapsedTime = TimeSpan.Zero;
}
```

`elapsedTime` compensates for how long the report took to arrive. When the clock skew or
transit delay exceeds `TimeSyncOffset` (2000 ms), the compensation is thrown away entirely and
the client's position is treated as current.

Note the interaction with Finding 2: the moment this branch fires, the position error grows by
exactly the transit time that was just discarded — which then trips the 500 ms drift check.
A device whose clock is merely wrong (a Fire TV stick that lost NTP, say) is guaranteed to be
force-seeked in a loop.

`_logger.LogWarning` is the only outward sign, and it names a session id, not a device.

---

## Finding 4: one stalled member blocks the entire group indefinitely

**Location:** [`Group.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/SyncPlay/Group.cs#L436-L455), [`WaitingGroupState.cs:471-479`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L471-L479)

`IsBuffering()` returns true if **any** member is buffering and has not set `IgnoreGroupWait`.
While that holds, every ready client is told to pause when ready. There is **no timeout**. If a
member's stream dies without the session ending — a stick that drops off wifi, a transcode that
wedges — the group waits forever.

Jellyfin does provide the escape hatch: `IgnoreWaitGroupRequest` sets the flag and immediately
re-evaluates, resuming the group
([`WaitingGroupState.cs:655-678`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L655-L678)).
But it is only ever sent when a **user manually presses a button in a client that exposes it** —
and most clients do not.

---

## Finding 5: groups can only be assembled by the people joining them

**Location:** [`SyncPlayController.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Jellyfin.Api/Controllers/SyncPlayController.cs)

Every SyncPlay endpoint acts on the **calling session**. To watch together, each person must,
on their own device: open settings, find SyncPlay, list groups, pick the right one, join. That
is the step non-technical family members do not complete.

The manager layer underneath does not have this restriction —
[`ISyncPlayManager`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/SyncPlay/ISyncPlayManager.cs)
takes an arbitrary `SessionInfo`:

```csharp
GroupInfoDto NewGroup(SessionInfo session, NewGroupRequest request, CancellationToken cancellationToken);
void JoinGroup(SessionInfo session, JoinGroupRequest request, CancellationToken cancellationToken);
```

The session-scoping is a property of the **HTTP layer**, not the domain. A server-side plugin can
therefore assemble a group on everyone's behalf. This is the mechanism FinParty's remote is
built on, and it is why it works with unmodified native clients.

### The trap

A session that joins a group but never reports itself ready leaves the group waiting forever
(Finding 4). A remote-control web page authenticates like any other client and so **owns a
session**. Adding it to the group deadlocks the party immediately.

FinParty therefore always hosts a party on a real playback device and never adds the remote
(`PartyManager.IsRemoteSession`).

---

## What FinParty does about each finding

| Finding | Response |
| --- | --- |
| 1 — unit mismatch | Documented for upstream; not worked around (it is masked on the links we care about) |
| 2 — drift tolerance | Retunes `MaxPlaybackOffset` per group from measured median RTT + jitter |
| 3 — timestamp discard | Retunes `TimeSyncOffset` in proportion, so the compensation survives |
| 4 — stalled member | Sends `IgnoreWaitGroupRequest` automatically after a configurable timeout, and reverses it when the member recovers |
| 5 — manual assembly | Phone remote that assembles the group server-side, for any client |

Findings 2 and 3 require writing to the private backing fields of get-only auto-properties,
since Jellyfin exposes no setter. That reflection is quarantined in a single class
(`SyncPlayReflector`) which reports its own health and degrades to stock behaviour rather than
throwing if a future Jellyfin release renames anything.

Findings 4 and 5 use **entirely public API** and are unaffected by version changes.

---

## Finding 6: a plugin DTO can stop Jellyfin from booting

Not a Jellyfin bug, but a trap sharp enough to be worth writing down: **FinParty 1.0.0 took a
live server down with it**, and the mechanism generalises to any plugin.

Jellyfin builds an OpenAPI document during startup. Swashbuckle derives each `schemaId` from a
type's **short name**, so a plugin DTO called `PlayRequest` collides with Jellyfin's own
[`MediaBrowser.Model.Session.PlayRequest`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Model/Session/PlayRequest.cs).
Schema generation throws, and because it happens while the host is starting, **Jellyfin never
binds its port**. The server does not start with the plugin disabled — it does not start at all.
The only evidence is a stack trace in the log.

What makes it dangerous is how invisible it is beforehand. The plugin compiles, loads as an
assembly, and passes every unit test. The collision only exists once a real Jellyfin enumerates
every controller in the process — which also means **another plugin's DTO can collide with
yours**, so a name that is unique against Jellyfin today can still break on a server with a
different plugin set.

Mitigations, in order of usefulness:

1. **Prefix every type reachable from a controller signature.** FinParty uses `FinParty*`
   (`FinPartyPlayRequest`, `FinPartyStateDto`, …). Internal types are unaffected — only what
   Swagger schematises matters.
2. **Test for it.** `SwaggerSchemaCollisionTests` walks the parameters and return types of every
   action, follows their properties, and fails if any short name matches a type in
   `MediaBrowser.Model` or `MediaBrowser.Controller`. It reproduces the collision at build time
   rather than at somebody's dinner time.
3. **Never restart a shared server to load an untested plugin build** without a rollback path
   that does not depend on that server being up. Removing a bad plugin needs filesystem access,
   which the Jellyfin API cannot give you once Jellyfin is the thing that is down.

---

## Finding 7: Moonfin cannot be made to start playing from idle

Live testing on an all-Moonfin household showed the headline UX — "tap a movie on your phone
and everyone's TV starts playing together" — **does not work with Moonfin**, and no server-side
plugin can make it. This is a client limitation, confirmed against Moonfin's own source.

Two server-side mechanisms could in principle start an idle device:

1. **SyncPlay group play.** Jellyfin's `PlayGroupRequest` sets the group's queue and broadcasts a
   play command. But SyncPlay only *synchronises clients already playing the item*. Moonfin's
   [`syncplay_manager.dart`](https://github.com/Moonfin-Client/Moonfin-Core/blob/main/lib/syncplay/syncplay_manager.dart)
   makes this explicit — `handlePlaybackCommand` returns early when
   `command.playlistItemId != currentPlaylistItemId`, and `handleGroupUpdate` on `groupJoined`
   only starts time-sync and ping loops; neither ever begins playback.
2. **Remote control** (`POST /Sessions/{id}/Playing`, "cast to device"). Requires the client to
   register a controllable session. Both Moonfin sessions tested report
   `SupportsRemoteControl = false` and never call `Sessions/Capabilities/Full`, so the command is
   accepted with `204` and silently dropped.

So an idle Moonfin device has **no server-reachable control channel at all**. What Moonfin *does*
support is being synchronised once the user has enabled SyncPlay and started the same item
themselves.

### What FinParty does about it

- For every party member Jellyfin *can* remote-control (web, Jellyfin Media Player, official
  apps, Kodi), FinParty issues a `PlayNow` remote command alongside the group play, so those
  devices start on their own. This is the full "TVs start themselves" experience where the client
  allows it.
- For members it cannot (Moonfin), the party state carries `NeedsManualStart`, and the remote
  says *"Open «title» on Risa's TV to sync up"* rather than spinning forever. Once that person
  presses play, FinParty's tuning keeps them in sync — which is the part that actually needed
  fixing over a VPN.

The honest one-line summary: **FinParty makes watching-together *work* for everyone, and makes it
*automatic* for every client except the ones that refuse server control.**

---

## Reproducing the measurements

The `GET /FinParty/api/health` endpoint reports, per session, the median RTT, the mean absolute
deviation (jitter), the worst sample, and the classified path type. Jitter above ~120 ms with a
`Tailscale` classification is the signature of a DERP relay rather than a direct connection;
confirm with `tailscale status` and look for `relay` on the row.
