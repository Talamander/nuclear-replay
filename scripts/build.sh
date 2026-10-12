#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
game_dir="${1:-/mnt/games/SteamLibrary/steamapps/common/Nuclear Option}"
test -f "$game_dir/NuclearOption_Data/Managed/Assembly-CSharp.dll"
test -f "$game_dir/BepInEx/core/0Harmony.dll"
dotnet build "$project_dir/NuclearReplay.csproj" -c Release --nologo "-p:GameDir=$game_dir"
mkdir -p "$project_dir/dist"
cp "$project_dir/bin/Release/netstandard2.1/NuclearReplay.dll" "$project_dir/dist/NuclearReplay.dll"
