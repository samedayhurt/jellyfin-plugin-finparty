#!/usr/bin/env python3
"""
Install and verify FinParty against a real Jellyfin server.

FinParty's interesting behaviour only exists at runtime: whether reflection binds
to this exact build's SyncPlay internals, whether real clients honour a
server-initiated group join, and whether the tolerances actually move once real
round-trip times are measured. None of that can be unit tested, so this drives it
end to end.

Nothing that starts playback on somebody's television runs without --yes.

Examples
--------
    # install (or upgrade) the plugin, restart, confirm it loaded
    ./tools/livetest.py --server http://192.168.8.153:8096 -u admin -p ... --stage install

    # read-only: health report, tuning binding, visible devices
    ./tools/livetest.py --server http://192.168.8.153:8096 -u admin -p ... --stage verify

    # full party test on two named devices
    ./tools/livetest.py --server ... -u admin -p ... --stage party \\
        --devices <sessionId>,<sessionId> --item <itemId> --yes
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

PLUGIN_NAME = "FinParty"
PLUGIN_GUID = "d5aefefe-1dac-4925-859f-70f70972a0d9"
REPO_NAME = "FinParty"
REPO_URL = "https://raw.githubusercontent.com/samedayhurt/jellyfin-plugin-finparty/main/manifest.json"

DEVICE_ID = f"finparty-livetest-{uuid.uuid4().hex[:8]}"

GREEN, YELLOW, RED, DIM, RESET = "\033[32m", "\033[33m", "\033[31m", "\033[2m", "\033[0m"

failures: list[str] = []


def ok(message: str) -> None:
    print(f"  {GREEN}PASS{RESET} {message}")


def warn(message: str) -> None:
    print(f"  {YELLOW}WARN{RESET} {message}")


def fail(message: str) -> None:
    print(f"  {RED}FAIL{RESET} {message}")
    failures.append(message)


def info(message: str) -> None:
    print(f"  {DIM}····{RESET} {message}")


def heading(message: str) -> None:
    print(f"\n{message}\n{'─' * len(message)}")


class Jellyfin:
    """A very small Jellyfin client, scoped to what this script needs."""

    def __init__(self, server: str, token: str | None = None) -> None:
        self.server = server.rstrip("/")
        self.token = token

    def _auth_header(self) -> str:
        header = (
            f'MediaBrowser Client="FinParty LiveTest", Device="livetest", '
            f'DeviceId="{DEVICE_ID}", Version="1.0.0"'
        )
        if self.token:
            header += f', Token="{self.token}"'
        return header

    def request(
        self,
        method: str,
        path: str,
        body: object | None = None,
        params: dict[str, str] | None = None,
        timeout: float = 20.0,
    ) -> tuple[int, object | str | None]:
        url = f"{self.server}{path}"
        if params:
            url += "?" + urllib.parse.urlencode(params)

        data = None
        if body is not None:
            data = json.dumps(body).encode()

        request = urllib.request.Request(url, data=data, method=method)
        request.add_header("Authorization", self._auth_header())
        request.add_header("Content-Type", "application/json")

        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                raw = response.read().decode(errors="replace")
                return response.status, _maybe_json(raw)
        except urllib.error.HTTPError as error:
            raw = error.read().decode(errors="replace")
            return error.code, _maybe_json(raw)
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            return 0, str(error)

    def login(self, username: str, password: str) -> bool:
        status, payload = self.request(
            "POST", "/Users/AuthenticateByName", {"Username": username, "Pw": password}
        )
        if status == 200 and isinstance(payload, dict):
            self.token = payload.get("AccessToken")
            user = payload.get("User") or {}
            self.user_id = user.get("Id")
            self.is_admin = bool((user.get("Policy") or {}).get("IsAdministrator"))
            return bool(self.token)
        return False


def _maybe_json(raw: str) -> object | str | None:
    if not raw:
        return None
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return raw


def cget(obj: object, name: str, default: object = None) -> object:
    """Case-insensitive field lookup.

    FinParty endpoints return a mix of casings — DTOs serialise as PascalCase
    (SessionId), anonymous projections as camelCase (sessionId). A test harness
    must not care which; reading the wrong case is exactly the bug that shipped
    a blank remote to users, so the harness reads either.
    """
    if not isinstance(obj, dict):
        return default
    if name in obj:
        return obj[name]
    lowered = name.lower()
    for key, value in obj.items():
        if key.lower() == lowered:
            return value
    return default


# --------------------------------------------------------------------------- stages


def stage_install(jf: Jellyfin, timeout: float) -> None:
    heading("Stage: install")

    if not getattr(jf, "is_admin", False):
        fail("this account is not an administrator; installing needs elevation")
        return

    status, repos = jf.request("GET", "/Repositories")
    if status != 200 or not isinstance(repos, list):
        fail(f"could not read plugin repositories (HTTP {status})")
        return

    if any((r or {}).get("Url") == REPO_URL for r in repos):
        ok("FinParty repository already registered")
    else:
        repos.append({"Name": REPO_NAME, "Url": REPO_URL, "Enabled": True})
        status, _ = jf.request("POST", "/Repositories", repos)
        if status in (200, 204):
            ok("registered the FinParty repository")
        else:
            fail(f"could not register the repository (HTTP {status})")
            return

    # Give the server a moment to pull the manifest before asking for the package.
    time.sleep(3)

    status, package = jf.request(
        "GET", f"/Packages/{urllib.parse.quote(PLUGIN_NAME)}", params={"assemblyGuid": PLUGIN_GUID}
    )
    if status == 200 and isinstance(package, dict):
        versions = [v.get("version") for v in (package.get("versions") or [])]
        ok(f"manifest visible to the server; versions offered: {versions or 'none'}")
        if not versions:
            fail("the server sees the repository but no compatible version — check targetAbi")
            return
    else:
        fail(f"the server cannot see FinParty in the catalogue (HTTP {status}): {package}")
        return

    status, payload = jf.request(
        "POST",
        f"/Packages/Installed/{urllib.parse.quote(PLUGIN_NAME)}",
        params={"assemblyGuid": PLUGIN_GUID},
    )
    if status in (200, 204):
        ok("install accepted")
    elif status == 409:
        warn("that version is already installed")
    else:
        fail(f"install rejected (HTTP {status}): {payload}")
        return

    info("restarting Jellyfin so the plugin loads")
    jf.request("POST", "/System/Restart")

    if wait_until_up(jf, timeout):
        ok("server came back")
    else:
        fail(f"server did not come back within {timeout:.0f}s")
        return

    status, plugins = jf.request("GET", "/Plugins")
    if status == 200 and isinstance(plugins, list):
        mine = [p for p in plugins if (p or {}).get("Id", "").replace("-", "").lower()
                == PLUGIN_GUID.replace("-", "").lower()]
        if mine:
            plugin = mine[0]
            state = plugin.get("Status", "unknown")
            if str(state).lower() in ("active", "0"):
                ok(f"plugin loaded: {plugin.get('Name')} {plugin.get('Version')} ({state})")
            else:
                fail(f"plugin is present but not active: status={state}")
        else:
            fail("plugin is not in the installed list after restart")
    else:
        fail(f"could not list plugins (HTTP {status})")

    status, _ = jf.request("GET", "/FinParty")
    if status == 200:
        ok("the party remote is being served at /FinParty")
    else:
        fail(f"/FinParty did not serve the remote (HTTP {status})")


def wait_until_up(jf: Jellyfin, timeout: float) -> bool:
    deadline = time.time() + timeout
    time.sleep(5)
    while time.time() < deadline:
        status, _ = jf.request("GET", "/System/Info/Public", timeout=5)
        if status == 200:
            return True
        time.sleep(3)
    return False


def stage_verify(jf: Jellyfin) -> dict | None:
    heading("Stage: verify (read-only)")

    status, health = jf.request("GET", "/FinParty/api/health")
    if status != 200 or not isinstance(health, dict):
        fail(f"health endpoint failed (HTTP {status}): {health}")
        return None

    ok("health endpoint responded")

    # The single most important runtime question: did reflection bind on THIS build?
    if health.get("tuningActive"):
        ok(f"latency tuning is ACTIVE — {health.get('syncPlayInternals')}")
    else:
        fail(
            "latency tuning is NOT active — "
            f"{health.get('syncPlayInternals')}. Parties will run with Jellyfin's fixed 500 ms "
            "tolerance, so the main fix is not in effect."
        )

    for finding in health.get("findings") or []:
        icon = {"ok": ok, "warn": warn, "problem": fail}.get(finding.get("severity"), info)
        # Findings about the environment are not test failures; report them as warnings.
        (warn if icon is fail else icon)(f"{finding.get('title')}: {finding.get('detail')}")

    links = health.get("links") or []
    info(f"{len(links)} controllable session(s) visible")
    for link in links:
        info(
            f"  {link.get('device')} [{link.get('client')}] {link.get('user')} "
            f"path={link.get('link')} rtt={link.get('latencyMs')}ms "
            f"jitter={link.get('jitterMs')}ms transcoding={link.get('transcoding')}"
        )

    status, devices = jf.request("GET", "/FinParty/api/devices")
    if status == 200 and isinstance(devices, list):
        ok(f"device list returned {len(devices)} candidate(s)")
        for device in devices:
            print(
                f"       {cget(device, 'sessionId')}  {cget(device, 'deviceName')!r} "
                f"({cget(device, 'client')}) user={cget(device, 'userName')} "
                f"inParty={cget(device, 'inParty')}"
            )
    else:
        fail(f"device list failed (HTTP {status}): {devices}")

    status, library = jf.request("GET", "/FinParty/api/library", params={"limit": "3"})
    if status == 200 and isinstance(library, list) and library:
        ok(f"library browse works (e.g. {cget(library[0], 'name')!r})")
    elif status == 200:
        warn("library browse returned nothing — is the library empty for this user?")
    else:
        fail(f"library browse failed (HTTP {status}): {library}")

    search_check(jf)

    return health


def verify_devices_actually_play(jf: Jellyfin, device_ids: list[str], settle: float = 20.0) -> None:
    """Confirm each targeted device's session really started playing.

    Reproduces the most important live finding: a native client (e.g. Moonfin for
    Android TV) can accept the server-side group join and the group reports Playing,
    yet an idle client never loads the queued item, so the television stays dark.
    Fails loudly with that exact diagnosis rather than trusting the group state.
    """
    heading("Stage: does the media actually start on the device?")
    deadline = time.time() + settle
    wanted = set(device_ids)
    playing: set[str] = set()

    while time.time() < deadline and playing != wanted:
        time.sleep(3)
        status, sessions = jf.request("GET", "/Sessions")
        if status != 200 or not isinstance(sessions, list):
            continue
        for session in sessions:
            sid = cget(session, "Id")
            if sid in wanted and cget(session, "NowPlayingItem"):
                playing.add(sid)

    for sid in device_ids:
        if sid in playing:
            ok(f"device {sid} is actually playing the item")
        else:
            fail(
                f"device {sid} JOINED the party but never started playing. "
                "The group is Playing server-side, but this client did not load the "
                "queued item — the 'TVs join by themselves' promise fails for it. "
                "Likely the client ignores a server-initiated SyncPlay queue when idle; "
                "starting playback may need a remote-control Play command first."
            )


def search_check(jf: Jellyfin) -> None:
    """Exercise library search the way the remote's search box does."""
    status, base = jf.request("GET", "/FinParty/api/library", params={"limit": "1"})
    if status != 200 or not isinstance(base, list) or not base:
        warn("cannot run search check — library browse returned nothing")
        return

    term = str(cget(base[0], "name", "")).split(" ")[0]
    if not term:
        return

    status, results = jf.request("GET", "/FinParty/api/library", params={"q": term, "limit": "10"})
    if status != 200 or not isinstance(results, list):
        fail(f"search for {term!r} failed (HTTP {status}): {results}")
        return

    if results:
        ok(f"search {term!r} returned {len(results)} result(s) (e.g. {cget(results[0], 'name')!r})")
    else:
        fail(f"search {term!r} returned nothing, though it came from a real title")


def stage_party(jf: Jellyfin, device_ids: list[str], item_id: str | None, confirmed: bool) -> None:
    heading("Stage: party (drives real devices)")

    if not confirmed:
        fail("refusing to start playback on real devices without --yes")
        return

    if not device_ids:
        fail("no --devices given; run --stage verify first to list session ids")
        return

    body: dict[str, object] = {"sessionIds": device_ids, "name": "FinParty live test"}
    if item_id:
        body["itemId"] = item_id

    status, payload = jf.request("POST", "/FinParty/api/parties", body)
    if status != 200 or not isinstance(payload, dict):
        fail(f"could not create the party (HTTP {status}): {payload}")
        return

    party = cget(payload, "party") or {}
    invites = cget(payload, "invites") or {}
    group_id = cget(party, "groupId")

    ok(f"party created: code {cget(party, 'code')} group {group_id}")

    joined = cget(invites, "joined") or []
    for session_id, reason in (cget(invites, "failed") or {}).items():
        fail(f"device {session_id} could not join: {reason}")

    if len(joined) == len(device_ids):
        ok(f"all {len(joined)} device(s) were joined server-side")
    else:
        warn(f"{len(joined)} of {len(device_ids)} device(s) joined")

    try:
        # Watch the party settle. The tuning snapshot only appears once the tuner has
        # seen the group, and observedRtt only once clients have reported pings.
        seen_tuning = False
        for attempt in range(15):
            time.sleep(2)
            status, state = jf.request("GET", f"/FinParty/api/parties/{group_id}")
            if status != 200 or not isinstance(state, dict):
                fail(f"could not read party state (HTTP {status}): {state}")
                break

            members = cget(state, "members") or []
            tuning = cget(state, "tuning")
            buffering = [cget(m, "userName") for m in members if cget(m, "isBuffering")]

            line = (
                f"t+{(attempt + 1) * 2:>3}s  state={cget(state, 'state'):<8} "
                f"members={len(members)} pos={cget(state, 'positionSeconds', 0):.0f}s "
                f"buffering={buffering or '-'}"
            )
            if tuning:
                line += (
                    f" | tolerance={cget(tuning, 'maxPlaybackOffsetMs')}ms "
                    f"rtt={cget(tuning, 'observedRttMs')}ms "
                    f"jitter={cget(tuning, 'observedJitterMs')}ms"
                )
            info(line)

            if tuning and not seen_tuning:
                seen_tuning = True
                ok(f"tuner picked the group up: {cget(tuning, 'explanation')}")
                if (cget(tuning, "maxPlaybackOffsetMs") or 0) > 500:
                    ok(
                        f"tolerance is {cget(tuning, 'maxPlaybackOffsetMs')}ms, wider than "
                        "Jellyfin's fixed 500ms"
                    )

            if cget(state, "state") == "Playing" and not buffering:
                ok("party reached Playing with nobody buffering")
                break

        if not seen_tuning:
            fail("the tuner never produced a snapshot for this group")

        # The make-or-break check: SyncPlay can report the GROUP as Playing while a
        # client that was idle never actually loads the media. Verify each target
        # device's own session really entered playback — a green group with dark TVs
        # is the failure that matters most to a family.
        if item_id:
            verify_devices_actually_play(jf, device_ids)

        if item_id:
            heading("Stage: transport")
            for action in ("pause", "resume"):
                status, state = jf.request("POST", f"/FinParty/api/parties/{group_id}/{action}")
                if status == 200:
                    time.sleep(2)
                    _, after = jf.request("GET", f"/FinParty/api/parties/{group_id}")
                    reported = after.get("state") if isinstance(after, dict) else "?"
                    ok(f"{action} accepted (state now {reported})")
                else:
                    fail(f"{action} failed (HTTP {status}): {state}")

    finally:
        heading("Cleanup")
        status, _ = jf.request("POST", f"/FinParty/api/parties/{group_id}/end")
        if status == 200:
            ok("party ended and devices released")
        else:
            warn(f"could not end the party cleanly (HTTP {status}) — check the server")


# --------------------------------------------------------------------------- main


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Install and verify FinParty against a live Jellyfin server."
    )
    parser.add_argument("--server", required=True, help="e.g. http://192.168.8.153:8096")
    parser.add_argument("-u", "--user", required=True, help="Jellyfin username")
    parser.add_argument("-p", "--password", default="", help="Jellyfin password")
    parser.add_argument(
        "--stage",
        default="verify",
        choices=("install", "verify", "party", "all"),
        help="which stage to run (default: verify)",
    )
    parser.add_argument("--devices", default="", help="comma-separated session ids for --stage party")
    parser.add_argument("--item", default=None, help="item id to play")
    parser.add_argument("--restart-timeout", type=float, default=180.0)
    parser.add_argument(
        "--yes", action="store_true", help="required before anything drives real devices"
    )
    args = parser.parse_args()

    jf = Jellyfin(args.server)

    heading(f"FinParty live test against {jf.server}")

    status, public = jf.request("GET", "/System/Info/Public")
    if status != 200 or not isinstance(public, dict):
        fail(f"server is not reachable (HTTP {status}): {public}")
        return 2
    ok(f"{public.get('ServerName')} running Jellyfin {public.get('Version')}")

    if not str(public.get("Version", "")).startswith("10.11"):
        warn(
            f"FinParty targets 10.11.x; this server reports {public.get('Version')}. "
            "Latency tuning may not bind."
        )

    if not jf.login(args.user, args.password):
        fail("authentication failed")
        return 2
    ok(f"signed in as {args.user} (admin={getattr(jf, 'is_admin', False)})")

    stages = ("install", "verify", "party") if args.stage == "all" else (args.stage,)

    if "install" in stages:
        stage_install(jf, args.restart_timeout)

    if "verify" in stages:
        stage_verify(jf)

    if "party" in stages:
        device_ids = [d.strip() for d in args.devices.split(",") if d.strip()]
        stage_party(jf, device_ids, args.item, args.yes)

    heading("Result")
    if failures:
        print(f"  {RED}{len(failures)} failure(s){RESET}")
        for failure in failures:
            print(f"    - {failure}")
        return 1

    print(f"  {GREEN}all checks passed{RESET}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\ninterrupted")
        sys.exit(130)
