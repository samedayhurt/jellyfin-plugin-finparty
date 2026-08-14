# FinParty

**SyncPlay watch parties for Jellyfin that actually survive Tailscale and WireGuard — and that
your family can start without being talked through it.**

FinParty does two things:

1. **Fixes SyncPlay over high-latency links.** Jellyfin's sync tolerances are compile-time
   constants sized for a LAN. FinParty measures each party's real round-trip time and jitter,
   then retunes them per group. It also stops one stalled device from freezing everyone.
2. **Adds a party remote anyone can use.** A phone-friendly page at `/FinParty`. Pick who's
   watching, pick something to watch, tap play. Everyone's TV joins by itself — no settings
   menus, no group lists, no instructions.

> Jellyfin's own SyncPlay requires every participant to find and join the group from their own
> device. That's the step people don't complete. FinParty assembles the group server-side, so it
> works with **unmodified clients** — including native ones like Moonfin, Swiftfin, Findroid and
> the official Android TV app.

---

## Why it breaks without this

Jellyfin hard-codes a **500 ms** playback drift tolerance
([`Group.cs:103`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/SyncPlay/Group.cs#L103)).
When a client's reported position is further off than that, the server force-seeks it.

Over a Tailscale link that has fallen back to a DERP relay, round-trip time runs 80–300 ms with
heavy jitter — so the *measurement error alone* approaches the threshold. The server "corrects" a
client that was never out of position, which causes a real buffer, which produces a genuinely late
report, which triggers another correction.

That feedback loop is the "everyone keeps jumping around" symptom. FinParty widens the tolerance
to match the link, and the loop stops.

**Full teardown with source citations, including an unreported upstream unit-mismatch bug:
[`docs/FINDINGS.md`](docs/FINDINGS.md).**

---

## Requirements

- Jellyfin **10.11.x** (built and tested against 10.11.11)
- Any client with SyncPlay support, for playback
- Any phone with a browser, for the remote

---

## Install

### From the plugin repository (recommended)

1. Jellyfin → **Dashboard → Plugins → Repositories → +**
2. Add:
   - **Name:** `FinParty`
   - **URL:** `https://raw.githubusercontent.com/samedayhurt/jellyfin-plugin-finparty/main/manifest.json`
3. **Catalog → General → FinParty → Install**
4. Restart Jellyfin.

### Manually

Download the latest zip from [Releases](https://github.com/samedayhurt/jellyfin-plugin-finparty/releases),
extract it into a folder inside your Jellyfin plugin directory, and restart:

```
<jellyfin-config>/plugins/FinParty/Jellyfin.Plugin.FinParty.dll
```

Docker users: that's the `/config` bind mount, so `/config/plugins/FinParty/`.

---

## Using it

Send your family this link and nothing else:

```
http://your-server:8096/FinParty
```

They sign in once with their normal Jellyfin name and password. After that:

**Start a watch party** → tap the people who should join → pick something → done. Everyone's app
switches over on its own.

The party screen shows a four-letter code anyone else can use to join, big play/pause controls,
and a live list of who's ready — with plain-language status like *"Waiting for Grandma's TV…"*
instead of buffering spinners.

**Connection check** runs the network doctor: per-device latency, jitter, path type
(LAN / Tailscale / VPN / internet), whether the server is transcoding for them, and what to do
about each problem it finds.

### Admin settings

**Dashboard → Plugins → FinParty.** Sensible defaults; the ones worth knowing:

| Setting | Default | What it does |
| --- | --- | --- |
| Tuning mode | Adaptive | Sizes tolerances from measured latency. `Fixed` applies your numbers verbatim; `Off` restores stock Jellyfin behaviour. |
| Minimum playback tolerance | 1500 ms | Floor for drift tolerance (Jellyfin's is 500). |
| Adaptive ceiling | 4000 ms | Stops one bad connection making the whole party sloppy. |
| Don't let one stuck device freeze everyone | on | After 25 s, the party continues and the stuck device rejoins when it catches up. |
| Grant SyncPlay access automatically | on | Saves walking someone through a settings page. |
| Let non-admins add other people's devices | on | Still requires Jellyfin's own remote-control permission. |

---

## How it works

```
Phone browser  ──HTTP──►  FinParty plugin  ──ISyncPlayManager──►  SyncPlay group
   (remote)                     │                                        │
                                │                                  WebSocket
                                ▼                                        ▼
                        PartyTuner (2 s loop)                    Apple TV / Fire TV /
                    measures RTT, retunes group,                   phones / browsers
                       breaks stalled waits                       (unmodified clients)
```

The remote is deliberately **never a member of the group**. A session that joins but never
reports itself ready would leave the group waiting forever, so parties are always hosted by a
real playback device and the remote acts through it.

### About the reflection

Findings 2 and 3 need to write to `Group.MaxPlaybackOffset` and `Group.TimeSyncOffset`, which
Jellyfin declares as get-only auto-properties with no configuration surface. There is no
supported way to change them, so FinParty writes the compiler-generated backing fields.

All of that lives in **one class** (`SyncPlayReflector`). It reports its own health, and if a
future Jellyfin release renames anything it logs a warning and degrades to stock behaviour rather
than throwing. The stall breaker and the party assembly — the two features people notice most —
use **entirely public API** and are unaffected.

You can see the current state at **Dashboard → Plugins → FinParty**, or via
`GET /FinParty/api/health`.

---

## API

All endpoints require normal Jellyfin authentication. `/FinParty` itself is anonymous (it's just
the page shell; every data call is authenticated).

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/FinParty` | The party remote |
| `GET` | `/FinParty/api/devices` | Devices the caller may add to a party |
| `GET` | `/FinParty/api/parties` | Live parties |
| `POST` | `/FinParty/api/parties` | Start a party |
| `GET` | `/FinParty/api/parties/{id}` | Party state |
| `POST` | `/FinParty/api/parties/{id}/invite` | Add devices |
| `DELETE` | `/FinParty/api/parties/{id}/members/{sessionId}` | Remove a device |
| `POST` | `/FinParty/api/parties/{id}/play` | Start playback |
| `POST` | `/FinParty/api/parties/{id}/pause` · `/resume` · `/seek` | Transport |
| `POST` | `/FinParty/api/parties/{id}/end` | End the party |
| `GET` | `/FinParty/api/code/{code}` | Resolve a join code |
| `GET` | `/FinParty/api/health` | Network doctor |
| `GET` | `/FinParty/api/library?q=` | Find something to watch |

### Permissions

Pulling **someone else's** device into a party requires either Jellyfin administrator, or
Jellyfin's `EnableRemoteControlOfOtherUsers` permission with the plugin's
*"let non-administrators add other people's devices"* setting enabled. Your own devices are
always allowed. Admins can turn the whole thing off.

---

## Building

Requires the .NET 9 SDK (Jellyfin 10.11 targets .NET 9 — not 8).

```bash
dotnet test                                    # 31 tests
dotnet publish -c Release
./build/package.sh 1.0.0.0                     # produces dist/finparty_1.0.0.0.zip + checksum
```

## Verifying a real install

The parts that matter most can't be unit tested: whether reflection binds to *your* build,
whether your clients honour a server-initiated group join, and whether the tolerances actually
move once real round-trip times come in. `tools/livetest.py` drives all of it over the API.

```bash
# read-only: health report, tuning binding, visible devices
./tools/livetest.py --server http://jellyfin:8096 -u admin -p ... --stage verify

# install (or upgrade) from the repository, restart, confirm it loaded
./tools/livetest.py --server http://jellyfin:8096 -u admin -p ... --stage install

# full party test on real devices — note the required --yes
./tools/livetest.py --server http://jellyfin:8096 -u admin -p ... --stage party \
    --devices <sessionId>,<sessionId> --item <itemId> --yes
```

`--stage verify` is safe to run any time; it only reads. Nothing that starts playback on
somebody's television runs without `--yes`.

The single most important line in its output is whether **latency tuning is ACTIVE** — if it
isn't, the drift fix is not in effect and parties are running on stock tolerances.

---

## Troubleshooting

**"Nobody's around" on the device picker.** Devices appear once their app is open and has checked
in. Sessions that can't be told what to play (`SupportsMediaControl == false`) are hidden.

**The party joins but playback never starts.** Usually one device is transcoding over the tunnel.
Run the connection check; if it flags transcoding, matching everyone's quality setting to the
source normally fixes it.

**Tuning shows as inactive.** FinParty couldn't bind to Jellyfin's SyncPlay internals — almost
always a Jellyfin version change. Parties still work, with stock tolerances. Please open an issue
with your exact version.

**Stalls only over the tunnel, never on the LAN.** Suspect MTU. From a client:
`ping -M do -s 1400 <server-tailscale-ip>`. If that fails but `-s 1200` succeeds, clamp MSS on the
router rather than changing Jellyfin.

---

## Licence

MIT. See [LICENSE](LICENSE).

Not affiliated with the Jellyfin project.
