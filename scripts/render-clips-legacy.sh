#!/usr/bin/env bash
# Render several ranges of a network demo in ONE game run (one fast-forward pass), HUD hidden, silent.
#
# Usage: scripts/render-clips.sh <demo.ncdemo> <ranges> <outdir> [shots.json] [fps]
#   ranges: "from:to,from:to,..." in replay seconds, non-overlapping (sorted automatically).
# Writes <outdir>/clip_<from>-<to>.mp4 for each range. Set WIDTH/HEIGHT for vertical (1080x1920).
# Frames go to TMPDIR (60 fps PNG is ~3 MB a frame; point TMPDIR at a big disk).
set -euo pipefail
demo="$(realpath "$1")"; ranges="$2"; outdir="$(realpath -m "$3")"; shots="${4:-}"; fps="${5:-60}"
here="$(cd "$(dirname "$0")" && pwd)"
frames="$(mktemp -d "${TMPDIR:-/tmp}/nc-frames.XXXXXX")"
trap 'rm -rf "$frames"' EXIT
mkdir -p "$outdir"
sorted=$(tr ',' '\n' <<<"$ranges" | sort -t: -k1,1g | paste -sd,)
args=(-ncReplay "$demo" -ncNoHud -ncReplayFrames "$frames" "$fps" -ncCaptureRanges "$sorted" -ncReplayQuit)
[[ -n "$shots" ]] && args+=(-ncShots "$(realpath "$shots")")
"$here/run-headless.sh" "${args[@]}" >/dev/null 2>&1 || true   # gamescope may segfault on exit; frames decide success
i=0; failed=0
IFS=, read -ra list <<<"$sorted"
for r in "${list[@]}"; do
    dir="$frames/$(printf 'range_%02d' "$i")"; i=$((i + 1))
    out="$outdir/clip_${r/:/-}.mp4"
    if ! compgen -G "$dir/frame_*.png" >/dev/null; then echo "$r: no frames exported" >&2; failed=1; continue; fi
    nice -n 19 ffmpeg -loglevel error -y -framerate "$fps" -i "$dir/frame_%06d.png" -c:v libx264 -crf 16 -preset slow -pix_fmt yuv420p "$out"
    rm -rf "$dir"
    echo "$out"
done
exit $failed
