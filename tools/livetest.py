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
                f"       {device.get('sessionId')}  {device.get('deviceName')!r} "
                f"({device.get('client')}) user={device.get('userName')} "
                f"inParty={device.get('inParty')}"
            )
    else:
        fail(f"device list failed (HTTP {status}): {devices}")

    status, library = jf.request("GET", "/FinParty/api/library", params={"limit": "3"})
    if status == 200 and isinstance(library, list) and library:
        ok(f"library browse works (e.g. {library[0].get('name')!r})")
    elif status == 200:
        warn("library browse returned nothing — is the library empty for this user?")
    else:
        fail(f"library browse failed (HTTP {status}): {library}")

    return health


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

    party = payload.get("party") or {}
    invites = payload.get("invites") or {}
    group_id = party.get("groupId")

    ok(f"party created: code {party.get('code')} group {group_id}")

    joined = invites.get("joined") or []
    for session_id, reason in (invites.get("failed") or {}).items():
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

            members = state.get("members") or []
            tuning = state.get("tuning")
            buffering = [m.get("userName") for m in members if m.get("isBuffering")]

            line = (
                f"t+{(attempt + 1) * 2:>3}s  state={state.get('state'):<8} "
                f"members={len(members)} pos={state.get('positionSeconds', 0):.0f}s "
                f"buffering={buffering or '-'}"
            )
            if tuning:
                line += (
                    f" | tolerance={tuning.get('maxPlaybackOffsetMs')}ms "
                    f"rtt={tuning.get('observedRttMs')}ms "
                    f"jitter={tuning.get('observedJitterMs')}ms"
                )
            info(line)

            if tuning and not seen_tuning:
                seen_tuning = True
                ok(f"tuner picked the group up: {tuning.get('explanation')}")
                if tuning.get("maxPlaybackOffsetMs", 0) > 500:
                    ok(
                        f"tolerance is {tuning.get('maxPlaybackOffsetMs')}ms, wider than "
                        "Jellyfin's fixed 500ms"
                    )

            if state.get("state") == "Playing" and not buffering:
                ok("party reached Playing with nobody buffering")
                break

        if not seen_tuning:
            fail("the tuner never produced a snapshot for this group")

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
