#!/usr/bin/env bash
# Run Nuclear Option off-screen and silent for automated record/replay tests.
#
# The game runs inside a headless gamescope session (no window on the desktop, no focus change)
# with Wine audio drivers disabled. Steam must already be running for Steamworks.
# Usage: scripts/run-headless.sh [game arguments...]
#   record: scripts/run-headless.sh -autoHost -mission "05. Furball" -state SinglePlayer -socket Offline -closeafter 150
#   replay: scripts/run-headless.sh -ncReplay /path/demo.ncdemo -ncReplayShots /path/shots 10 -ncReplayQuit -closeafter 300
set -euo pipefail
game_dir="${GAME_DIR:-/mnt/games/SteamLibrary/steamapps/common/Nuclear Option}"
proton="${PROTON:-$HOME/.local/share/Steam/steamapps/common/Proton - Experimental/proton}"
compat_data="${COMPAT_DATA:-/mnt/games/SteamLibrary/steamapps/compatdata/2168680}"
width="${WIDTH:-1920}"
height="${HEIGHT:-1080}"

# Several agents may render at once; queue them on one lock. The lock fd stays open through exec,
# so it is held until the game exits.
exec 9>"${XDG_RUNTIME_DIR:-/tmp}/nuclear-context-game.lock"
flock 9
if pgrep -x 'NuclearOption.e(xe)?' >/dev/null; then   # match the process name, not command lines that mention it
    echo 'Nuclear Option is already running; refusing to start a second copy.' >&2
    exit 1
fi
pgrep -x steam >/dev/null || pgrep -f 'steamwebhelper' >/dev/null || { echo 'Steam is not running.' >&2; exit 1; }

export STEAM_COMPAT_CLIENT_INSTALL_PATH="$HOME/.local/share/Steam"
export STEAM_COMPAT_DATA_PATH="$compat_data"
export SteamAppId=2168680 SteamGameId=2168680
# Lets Proton map the library to drive S: exactly as a Steam launch does (plugin paths use S:).
export STEAM_COMPAT_INSTALL_PATH="$game_dir" STEAM_COMPAT_LIBRARY_PATHS="$(dirname "$(dirname "$(dirname "$game_dir")")")"
# winhttp: BepInEx doorstop. winepulse/winealsa disabled: the game runs without audio output,
# unless NC_AUDIO_SINK names a PulseAudio sink to play into (for capturing game audio).
if [[ -n "${NC_AUDIO_SINK:-}" ]]; then
    export WINEDLLOVERRIDES="winhttp=n,b" PULSE_SINK="$NC_AUDIO_SINK"
else
    export WINEDLLOVERRIDES="winhttp=n,b;winepulse.drv=d;winealsa.drv=d"
fi
cd "$game_dir"
exec gamescope --backend headless -W "$width" -H "$height" -w "$width" -h "$height" -r 60 -- \
    "$proton" waitforexitandrun "$game_dir/NuclearOption.exe" \
    -screen-width "$width" -screen-height "$height" -screen-fullscreen 0 "$@"
