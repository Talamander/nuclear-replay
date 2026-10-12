#!/usr/bin/env bash
# Run Nuclear Option in a normal desktop window with audio, bypassing the Steam launch
# wrapper (no AB6 force feedback service). Used to watch replays interactively.
# Steam must already be running for Steamworks.
# Usage: scripts/run-windowed.sh -ncReplay 'S:\steamapps\common\Nuclear Option\NuclearReplay\demos\<file>.ncdemo'
set -euo pipefail
game_dir="${GAME_DIR:-/mnt/games/SteamLibrary/steamapps/common/Nuclear Option}"
proton="${PROTON:-$HOME/.local/share/Steam/steamapps/common/Proton - Experimental/proton}"
compat_data="${COMPAT_DATA:-/mnt/games/SteamLibrary/steamapps/compatdata/2168680}"

if pgrep -f 'NuclearOption\.exe' >/dev/null; then
    echo 'Nuclear Option is already running; refusing to start a second copy.' >&2
    exit 1
fi
pgrep -x steam >/dev/null || pgrep -f 'steamwebhelper' >/dev/null || { echo 'Steam is not running.' >&2; exit 1; }

export STEAM_COMPAT_CLIENT_INSTALL_PATH="$HOME/.local/share/Steam"
export STEAM_COMPAT_DATA_PATH="$compat_data"
export SteamAppId=2168680 SteamGameId=2168680
export STEAM_COMPAT_INSTALL_PATH="$game_dir" STEAM_COMPAT_LIBRARY_PATHS="$(dirname "$(dirname "$(dirname "$game_dir")")")"
export WINEDLLOVERRIDES="winhttp=n,b"
if pactl list short sinks 2>/dev/null | grep -q $'\tgame_sink\t'; then
    export PULSE_SINK=game_sink
fi
cd "$game_dir"
exec "$proton" waitforexitandrun "$game_dir/NuclearOption.exe" \
    -screen-width 1920 -screen-height 1080 -screen-fullscreen 0 "$@"
