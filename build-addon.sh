#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="$ROOT_DIR/dist"
TARGET_FRAMEWORK="net10.0"
ADDON_DLL="$ROOT_DIR/osu.Game.Rulesets.ReplayVaultAddon/bin/Release/$TARGET_FRAMEWORK/osu.Game.Rulesets.ReplayVaultAddon.dll"

mkdir -p "$DIST_DIR"

dotnet build "$ROOT_DIR/osu.Game.Rulesets.ReplayVaultAddon/osu.Game.Rulesets.ReplayVaultAddon.csproj" -c Release -m:1 /nr:false
cp "$ADDON_DLL" "$DIST_DIR/osu.Game.Rulesets.ReplayVaultAddon-$TARGET_FRAMEWORK.dll"

echo "Built addon DLL:"
ls -1 "$DIST_DIR/osu.Game.Rulesets.ReplayVaultAddon-$TARGET_FRAMEWORK.dll"
