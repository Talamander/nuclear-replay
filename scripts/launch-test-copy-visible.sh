#!/usr/bin/env bash
# Visible-window variant of launch-test-copy.sh, for a person trying the in-game replay viewer on the
# ISOLATED test copy of Nuclear Option (default /mnt/games/nc-gui-test).
#
# Point viewer launches at it with NC_VIEWER_WRAPPER=<this script>; `nr open` then runs
#   launch-test-copy-visible.sh <game arguments...>
# The game opens in a normal window on the desktop, with sound, under Proton with the test copy's own
# prefix. Steam must be running and signed in (Steamworks). Agents and tests use launch-test-copy.sh
# (headless gamescope, muted) instead; this script is for people.
#
# Environment:
#   NC_TEST_COPY  folder holding "Nuclear Option/" and pfx/ (default /mnt/games/nc-gui-test)
#   NC_TEST_LOG   log file for Proton output (default $NC_TEST_COPY/logs/visible-<time>.log)
#
# Holds the shared nuclear-context-game.lock until the game exits and refuses to start while another
# game holds it. Refuses to run against the main Steam install or the main prefix.
set -euo pipefail
copy="${NC_TEST_COPY:-/mnt/games/nc-gui-test}"
game="$copy/Nuclear Option"
compat="$copy/pfx"
proton="${PROTON:-$HOME/.local/share/Steam/steamapps/common/Proton - Experimental/proton}"
lock="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/nuclear-context-game.lock"

die() { echo "launch-test-copy-visible: $*" >&2; exit 1; }
case "$(realpath -m "$game")" in
    */steamapps/common/*) die "refusing to run a Steam library install: $game" ;;
esac
case "$(realpath -m "$compat")" in
    */steamapps/compatdata/*) die "refusing to use a Steam prefix: $compat" ;;
esac
[[ -f "$game/NuclearOption.exe" && -f "$game/winhttp.dll" ]] || die "no game copy with BepInEx at $game"
[[ -x "$proton" ]] || die "Proton not found at $proton"
pgrep -x steam >/dev/null || pgrep -x steamwebhelper >/dev/null || die "Steam is not running"

mkdir -p "$copy/logs" "$compat"
log="${NC_TEST_LOG:-$copy/logs/visible-$(date +%Y%m%d-%H%M%S).log}"

exec 9>"$lock"
flock -n 9 || die "another Nuclear Option session (a render, pictures or a test) holds the game lock; try again when it is done"
if grep -qxF 'NuclearOption.e' /proc/[0-9]*/comm 2>/dev/null; then   # exact name; the dedicated server is NuclearOptionSe
    die "Nuclear Option is already running"
fi

export STEAM_COMPAT_CLIENT_INSTALL_PATH="${STEAM_COMPAT_CLIENT_INSTALL_PATH:-$HOME/.local/share/Steam}"
export STEAM_COMPAT_DATA_PATH="$compat"
export STEAM_COMPAT_INSTALL_PATH="$game" STEAM_COMPAT_LIBRARY_PATHS="$copy"
export SteamAppId=2168680 SteamGameId=2168680
export WINEDLLOVERRIDES="winhttp=n,b"
export PROTON_USE_XALIA=0

echo "launch-test-copy-visible: $(date -Is) $*" >>"$log"
cd "$game"
exec "$proton" waitforexitandrun "$game/NuclearOption.exe" "$@" >>"$log" 2>&1
