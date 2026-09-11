#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="$ROOT_DIR/dist"

mkdir -p "$DIST_DIR"

dotnet build "$ROOT_DIR/osu.Game.Rulesets.ReplayVaultAddon/osu.Game.Rulesets.ReplayVaultAddon.csproj" -c Release -m:1 /nr:false
ADDON_DLL="$(find "$ROOT_DIR/osu.Game.Rulesets.ReplayVaultAddon/bin/Release" -type f -name 'osu.Game.Rulesets.ReplayVaultAddon.dll' -print -quit)"
cp "$ADDON_DLL" "$DIST_DIR/"

echo "Built addon DLL:"
ls -1 "$DIST_DIR"/osu.Game.Rulesets.ReplayVaultAddon.dll
