#!/usr/bin/env bash
#
# Packages FinParty into a Jellyfin-installable zip and prints the checksum the
# plugin repository manifest needs.
#
# Usage: ./build/package.sh [version]
#
set -euo pipefail

VERSION="${1:-1.0.0.0}"
# The git tag the release asset will hang off. Defaults to the version with any
# trailing ".0" trimmed, matching the usual vMAJOR.MINOR.PATCH tag style.
TAG="${2:-v${VERSION%.0}}"
GUID="d5aefefe-1dac-4925-859f-70f70972a0d9"
NAME="FinParty"
ABI="10.11.0.0"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/Jellyfin.Plugin.FinParty"
STAGE="$ROOT/dist/stage"
OUT="$ROOT/dist"
ZIP="$OUT/finparty_${VERSION}.zip"

command -v dotnet >/dev/null 2>&1 || { echo "error: dotnet SDK not on PATH" >&2; exit 1; }

echo "==> Building $NAME $VERSION"
rm -rf "$STAGE" "$ZIP"
mkdir -p "$STAGE"

dotnet publish "$PROJECT/Jellyfin.Plugin.FinParty.csproj" \
  -c Release \
  -p:Version="$VERSION" \
  -p:AssemblyVersion="$VERSION" \
  -p:FileVersion="$VERSION" \
  -o "$STAGE/publish" \
  --nologo

# Ship only our own assembly. Jellyfin provides everything else at runtime, and
# shipping a second copy of a host assembly breaks plugin loading.
cp "$STAGE/publish/Jellyfin.Plugin.FinParty.dll" "$STAGE/"
rm -rf "$STAGE/publish"

TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

cat > "$STAGE/meta.json" <<EOF
{
    "category": "General",
    "guid": "$GUID",
    "name": "$NAME",
    "description": "Watch parties that survive Tailscale, WireGuard and other high-latency links, with a phone remote any family member can use.",
    "overview": "SyncPlay that works over a VPN, plus a one-tap party remote.",
    "owner": "samedayhurt",
    "targetAbi": "$ABI",
    "timestamp": "$TIMESTAMP",
    "version": "$VERSION",
    "status": "Active",
    "autoUpdate": true,
    "changelog": ""
}
EOF

echo "==> Zipping"
mkdir -p "$OUT"
( cd "$STAGE" && zip -qr "$ZIP" . )
rm -rf "$STAGE"

# Jellyfin's plugin repository verifies downloads with an MD5 checksum.
if command -v md5sum >/dev/null 2>&1; then
  CHECKSUM="$(md5sum "$ZIP" | cut -d' ' -f1)"
else
  CHECKSUM="$(md5 -q "$ZIP")"
fi

echo
echo "  package  : $ZIP"
echo "  size     : $(du -h "$ZIP" | cut -f1)"
echo "  version  : $VERSION"
echo "  targetAbi: $ABI"
echo "  checksum : $CHECKSUM"
echo "  timestamp: $TIMESTAMP"
echo
echo "Add this to manifest.json under \"versions\":"
cat <<EOF
{
  "version": "$VERSION",
  "changelog": "",
  "targetAbi": "$ABI",
  "sourceUrl": "https://github.com/samedayhurt/jellyfin-plugin-finparty/releases/download/$TAG/finparty_$VERSION.zip",
  "checksum": "$CHECKSUM",
  "timestamp": "$TIMESTAMP"
}
EOF
