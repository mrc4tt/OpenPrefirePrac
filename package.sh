#!/usr/bin/env bash
# Builds Release and packages dist/OpenPrefirePrac.zip in the CounterStrikeSharp layout:
#   addons/counterstrikesharp/plugins/OpenPrefirePrac/...   (dll, deps, maps/, lang/, resources/)
# Also writes dist/release-notes.md from the matching CHANGELOG.md section.
#
# Usage: ./package.sh [X.Y.Z]   (version defaults to ModuleVersion; when given it must match)
set -euo pipefail
cd "$(dirname "$0")"

PLUGIN=OpenPrefirePrac
GEOIP_URL=https://github.com/P3TERX/GeoLite.mmdb/raw/download/GeoLite2-Country.mmdb

SOURCE_VERSION="$(grep -oP 'ModuleVersion => "\K[^"]+' OpenPrefirePrac.cs)"
VERSION="${1:-$SOURCE_VERSION}"
if [[ "$VERSION" != "$SOURCE_VERSION" ]]; then
    echo "ERROR: version $VERSION does not match ModuleVersion ($SOURCE_VERSION)" >&2
    exit 1
fi

NOTES="$(awk -v v="$VERSION" '$0 ~ "^## " { if (found) exit; found = ($2 == v); next } found' CHANGELOG.md)"
if [[ -z "${NOTES//[[:space:]]/}" ]]; then
    echo "ERROR: CHANGELOG.md has no \"## $VERSION\" section" >&2
    exit 1
fi

rm -rf dist bin/Release
dotnet build OpenPrefirePrac.csproj -c Release

STAGE=dist/stage
PLUGIN_DIR="$STAGE/addons/counterstrikesharp/plugins/$PLUGIN"
mkdir -p "$PLUGIN_DIR"
cp -r bin/Release/net10.0/. "$PLUGIN_DIR/"
cp -r maps lang resources "$PLUGIN_DIR/"
curl -fsSL --retry 3 -o "$PLUGIN_DIR/resources/GeoLite2-Country.mmdb" "$GEOIP_URL"

# Linux only, no debug symbols or backups.
find "$STAGE" \( -name '*.pdb' -o -name '*.bak' \) -delete
find "$STAGE" -type d -path '*/runtimes/*' -prune ! -name linux-x64 ! -name unix -exec rm -rf {} +

test -f "$PLUGIN_DIR/$PLUGIN.dll"
if [[ -d "$STAGE/addons/counterstrikesharp/configs" || -e "$PLUGIN_DIR/default_cfg.json" ]]; then
    echo "ERROR: config files must never be shipped" >&2
    exit 1
fi

(cd "$STAGE" && zip -qr "../$PLUGIN.zip" addons)
rm -rf "$STAGE"
printf '%s\n' "$NOTES" > dist/release-notes.md

unzip -l "dist/$PLUGIN.zip" | grep -v '/maps/.*\.json$'
echo "==> dist/$PLUGIN.zip ($VERSION)"
