# FinParty

**Keeps Jellyfin SyncPlay from stalling over Tailscale, WireGuard and other high-latency links.**

FinParty is headless. There is nothing to open and nothing to set up. Whenever a SyncPlay group
is active, it measures the real round-trip time to each member, widens Jellyfin's timing
tolerances to match the network, and stops one buffering device from freezing the whole group.

> **Scope, stated honestly.** FinParty tunes SyncPlay *server-side* — it changes how Jellyfin
> coordinates a group, not how any client behaves. It does **not** remote-control playback and is
> **not** a "start everyone's TV from my phone" remote. That was an earlier direction; testing on
> an all-[Moonfin](https://moonfin.io) household showed Moonfin ignores server-initiated playback
> commands, so a plugin cannot drive it. What a plugin *can* do — and what this does — is make the
> sync itself stop drifting and stalling once people are watching the same thing together.

## Why SyncPlay stalls over a VPN

Jellyfin hard-codes a **500 ms** playback-drift tolerance
([`Group.cs:103`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/SyncPlay/Group.cs#L103)),
with no configuration surface. Over a Tailscale link that has fallen back to a DERP relay,
round-trip time runs 80–300 ms with heavy jitter — so the *measurement error alone* approaches the
threshold. The server decides a client is out of position, force-seeks it, which causes a real
buffer, which produces a genuinely late report, which triggers another correction. That feedback
loop is the drifting-and-stalling you see. Widen the tolerance to fit the link and the loop stops.

Separately, if one device buffers and never reports ready, the group waits for it **with no
timeout** — so a single stuck stream freezes everyone. FinParty releases the group after a few
seconds and lets that device rejoin when it catches up.

Full teardown with source citations — including an unreported upstream unit-mismatch bug — is in
[`docs/FINDINGS.md`](docs/FINDINGS.md).

## What it does

- **Adaptive tuning.** Per group, sizes `MaxPlaybackOffset` / `TimeSyncOffset` / `DefaultPing`
  from the measured median RTT and jitter, instead of the single worst-case ping Jellyfin uses.
- **Stall breaker.** After a configurable timeout, a buffering member is released via SyncPlay's
  own "stop waiting for me" request, and re-included automatically once it recovers.
- **Network doctor.** `GET /FinParty/api/health` reports each session's latency, jitter, path type
  (LAN / Tailscale / VPN / internet), transcoding state and MTU traps — so you can see *why* a link
  is rough.

All of it runs from a single background service. The reflection needed to write Jellyfin's private
tolerance fields is quarantined in one class (`SyncPlayReflector`) that reports its own health and
degrades to stock behaviour rather than throwing if a future Jellyfin release renames anything.

## Requirements

- Jellyfin **10.11.x** (built and tested against 10.11.11, .NET 9)
- Any SyncPlay-capable client, watching together the normal way

## Install

Dashboard → Plugins → Repositories → **+**, add:

```
https://raw.githubusercontent.com/samedayhurt/jellyfin-plugin-finparty/main/manifest.json
```

Then Catalog → **FinParty** → Install, and restart Jellyfin. That's it — it starts working on the
next SyncPlay group. Settings (tuning mode, tolerances, stall-breaker timeout) live at
Dashboard → Plugins → **FinParty**; the defaults are tuned for a relayed VPN.

## Building

Requires the .NET 9 SDK (Jellyfin 10.11 targets .NET 9, not 8).

```bash
dotnet test                       # 34 tests
./build/package.sh 1.1.0.0        # dist/finparty_1.1.0.0.zip + checksum
```

## Licence

MIT. Not affiliated with the Jellyfin or Moonfin projects.
