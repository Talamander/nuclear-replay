#!/usr/bin/env bash
# Render part of a network demo to an MP4 with the game's own visuals, off-screen and silent.
#
# Usage: scripts/render-clip.sh <demo.ncdemo> <from-seconds> <to-seconds> <out.mp4> [shots.json] [fps]
# The shot list defaults to <demo>.shots.json when present. Seconds are replay time from demo start.
# Frames go to a temporary directory and are deleted after encoding. The game HUD is hidden.
set -euo pipefail
demo="$(realpath "$1")"; from="$2"; to="$3"; out="$(realpath -m "$4")"; shots="${5:-}"; fps="${6:-60}"
here="$(cd "$(dirname "$0")" && pwd)"
frames="$(mktemp -d "${TMPDIR:-/tmp}/nc-frames.XXXXXX")"
trap 'rm -rf "$frames"' EXIT
args=(-ncReplay "$demo" -ncNoHud -ncReplayFrames "$frames" "$fps" -ncCaptureRange "$from" "$to" -ncReplayQuit)
[[ -n "$shots" ]] && args+=(-ncShots "$(realpath "$shots")")
"$here/run-headless.sh" "${args[@]}" >/dev/null 2>&1 || true   # gamescope may segfault on exit; frames decide success
count=$(find "$frames" -name 'frame_*.png' | wc -l)
(( count > 0 )) || { echo "No frames were exported; see BepInEx/LogOutput.log." >&2; exit 1; }
ffmpeg -loglevel error -y -framerate "$fps" -i "$frames/frame_%06d.png" -c:v libx264 -crf 16 -preset slow -pix_fmt yuv420p "$out"
echo "$out: $count frames at $fps fps"
