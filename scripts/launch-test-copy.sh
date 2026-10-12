#!/usr/bin/env bash
# Launch wrapper for testing nr against an ISOLATED copy of Nuclear Option.
#
# Point nr at it with NC_LAUNCH_WRAPPER=<this script>; they then run
#   launch-test-copy.sh <game arguments...>
# instead of "steam -applaunch". The game runs inside a headless gamescope session (no window on the
# desktop, no focus change, no audio) with its own Proton prefix. Steam must be running and signed in
# (Steamworks; without it the replay does not start).
#
# Environment:
#   NC_TEST_COPY  folder holding "Nuclear Option/" and pfx/ (default /mnt/games/nc-gui-test)
#   NC_TEST_LOG   log file for gamescope/Proton output (default $NC_TEST_COPY/logs/launch-<time>.log)
#
# Holds the shared nuclear-context-game.lock until the game exits; waits when another session holds it.
# Refuses to run against the main Steam install or the main prefix.
set -euo pipefail
copy="${NC_TEST_COPY:-/mnt/games/nc-gui-test}"
game="$copy/Nuclear Option"
compat="$copy/pfx"   # a Proton compatdata folder (holds pfx/)
proton="${PROTON:-$HOME/.local/share/Steam/steamapps/common/Proton - Experimental/proton}"
lock="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/nuclear-context-game.lock"

die() { echo "launch-test-copy: $*" >&2; exit 1; }
case "$(realpath -m "$game")" in
    */steamapps/common/*) die "refusing to run a Steam library install: $game" ;;
esac
case "$(realpath -m "$compat")" in
    */steamapps/compatdata/*) die "refusing to use a Steam prefix: $compat" ;;
esac
[[ -f "$game/NuclearOption.exe" && -f "$game/winhttp.dll" ]] || die "no game copy with BepInEx at $game"
command -v gamescope >/dev/null || die "gamescope not found"
[[ -x "$proton" ]] || die "Proton not found at $proton"
pgrep -x steam >/dev/null || pgrep -x steamwebhelper >/dev/null || die "Steam is not running"

# Window size from the game arguments (preview and render jobs pass -screen-width/-screen-height).
width=1920 height=1080
args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
    case "${args[$i]}" in
        -screen-width) width="${args[$((i + 1))]:-$width}" ;;
        -screen-height) height="${args[$((i + 1))]:-$height}" ;;
    esac
done

mkdir -p "$copy/logs" "$compat"
log="${NC_TEST_LOG:-$copy/logs/launch-$(date +%Y%m%d-%H%M%S).log}"

exec 9>"$lock"
flock 9
if pgrep -x 'NuclearOption.e' >/dev/null; then
    die "Nuclear Option is already running"
fi

rt="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
unset WAYLAND_DISPLAY DISPLAY HYPRLAND_INSTANCE_SIGNATURE
export XDG_RUNTIME_DIR="$rt"
export DBUS_SESSION_BUS_ADDRESS="${DBUS_SESSION_BUS_ADDRESS:-unix:path=$rt/bus}"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="${STEAM_COMPAT_CLIENT_INSTALL_PATH:-$HOME/.local/share/Steam}"
export STEAM_COMPAT_DATA_PATH="$compat"
export STEAM_COMPAT_INSTALL_PATH="$game" STEAM_COMPAT_LIBRARY_PATHS="$copy"
export SteamAppId=2168680 SteamGameId=2168680
export WINEDLLOVERRIDES="winhttp=n,b;winepulse.drv=d;winealsa.drv=d"
export PROTON_USE_XALIA=0

echo "launch-test-copy: $(date -Is) ${width}x${height} $*" >>"$log"
cd "$game"
# gamescope is the process the caller started; killing it (or its tree) stops the game.
exec gamescope --backend headless -W "$width" -H "$height" -w "$width" -h "$height" -r 60 -- \
    "$proton" waitforexitandrun "$game/NuclearOption.exe" "$@" >>"$log" 2>&1
