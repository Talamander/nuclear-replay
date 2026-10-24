#!/usr/bin/env bash
# Capture the game's own audio for a network demo replay, aligned to replay time.
#
# Usage: scripts/render-audio.sh <demo.ncdemo> <out.wav> [shots.json]
# The replay runs headless in real time and plays into a private null sink, so nothing is heard
# on the desktop. The output starts at replay time 0. Game audio follows the camera, so use the
# same shot list as the video render. Steam must be running and the game closed.
set -euo pipefail
demo="$(realpath "$1")"; out="$(realpath -m "$2")"; shots="${3:-}"
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/nc-audio.XXXXXX")"
sink="nc_capture_$$"
module=$(pactl load-module module-null-sink "sink_name=$sink" "sink_properties=device.description=NuclearReplayCapture")
cleanup() {
    [[ -n "${recorder:-}" ]] && kill "$recorder" 2>/dev/null && wait "$recorder" 2>/dev/null
    pactl unload-module "$module" 2>/dev/null || true
    rm -rf "$work"
}
trap cleanup EXIT

parecord --device="$sink.monitor" --file-format=wav --rate=48000 --channels=2 "$work/raw.wav" &
recorder=$!
date -u +%s.%N > "$work/record-start"
args=(-ncReplay "$demo" -ncNoHud -ncReplayQuit -ncClockLog "$work/clock.txt")
[[ -n "$shots" ]] && args+=(-ncShots "$(realpath "$shots")")
NC_AUDIO_SINK="$sink" "${NC_RUNNER:-$here/run-headless.sh}" "${args[@]}" >/dev/null 2>&1 || true   # gamescope may segfault on exit
kill "$recorder"; wait "$recorder" 2>/dev/null || true; recorder=
[[ -s "$work/clock.txt" ]] || { echo "No replay clock was logged; see BepInEx/LogOutput.log." >&2; exit 1; }

# Output latency (NC_AUDIO_LATENCY, default 0.12 s, measured against explosion frames) is trimmed too.
# Each clock line is "replayTime utc". Replay time 0 sits at the median of (utc - replayTime).
offset=$(python3 - "$work/clock.txt" "$work/record-start" "${NC_AUDIO_LATENCY:-0.12}" <<'PY'
import sys, statistics
from datetime import datetime
start = float(open(sys.argv[2]).read())
zero = []
for line in open(sys.argv[1]):
    r, u = line.split()
    u = u.rstrip('Z')[:26]
    zero.append(datetime.fromisoformat(u + '+00:00').timestamp() - float(r))
print(f"{statistics.median(zero) - start + float(sys.argv[3]):.4f}")
PY
)
ffmpeg -loglevel error -y -ss "$offset" -i "$work/raw.wav" -c:a pcm_s16le "$out"
echo "$out: replay audio, offset $offset s into the capture"
