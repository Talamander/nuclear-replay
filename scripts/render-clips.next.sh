#!/usr/bin/env bash
# Render several ranges of a network demo in ONE game run (one fast-forward pass), HUD hidden, silent.
#
# Usage: scripts/render-clips.sh <demo.ncdemo> <ranges> <outdir> [shots.json] [fps]
#   ranges: "from:to,from:to,..." in replay seconds, sorted automatically. A range may carry its own
#           capture rate: "1940:1948@60" (slow-motion source). Overlapping ranges are rendered once as
#           their union and cut apart afterwards.
#   fps:    default 30 (the Shorts frame rate). Pass 60 only for slow-motion sources.
# Writes <outdir>/clip_<from>-<to>.mp4 for each range, each one as soon as its range is captured
# (written as .part and renamed, so a clip that exists is complete). Prints each clip path when it lands.
# Set WIDTH/HEIGHT for vertical (1080x1920). Big scratch data goes to TMPDIR.
#
# Speed path (default, RENDER_MODE=fast):
#   - frames go from the game straight into the encoder through a FIFO (no PNG files),
#   - the skip-ahead runs on a fixed replay step as fast as the game simulates (about 9-10x, was 4x),
#   - each range is encoded while the game continues to the next one,
#   - clips are cached in RENDER_CACHE keyed by demo + range + fps + size + the shots in effect, so a
#     range that was rendered before with the same camera is copied instead of rendered.
# Environment:
#   RENDER_MODE=legacy   the old path exactly: PNG frames, 4x wall-clock skip, x264 slow after the game exits
#   ENCODER=nvenc|x264   clip encoder (default nvenc when h264_nvenc works, else x264 veryfast)
#   FAST_FORWARD="8 60"  skip speed and fixed step rate: 8/60 = 0.133 replay s per frame, the step the old
#                        4x wall-clock skip took whenever the game ran at 30 fps. Measured about 10x real time.
#                        16/60 was faster (about 12x) but changed which missile the "auto" camera followed.
#   RENDER_CACHE=dir     clip cache (default /mnt/games/nuclear-option-content/render-cache); NO_CACHE=1 skips it
#   RENDER_TIMEOUT=s     kill the game after this many seconds (default: estimated from the ranges, generous)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
if [[ "${RENDER_MODE:-fast}" == legacy ]]; then exec "$here/render-clips-legacy.sh" "$@"; fi

demo="$(realpath "$1")"; ranges="$2"; outdir="$(realpath -m "$3")"; shots="${4:-}"; fps="${5:-30}"
width="${WIDTH:-1920}"; height="${HEIGHT:-1080}"
cache="${RENDER_CACHE:-/mnt/games/nuclear-option-content/render-cache}"
read -r ff_speed ff_step <<<"${FAST_FORWARD:-8 60}"
mkdir -p "$outdir"
[[ -n "$shots" ]] && shots="$(realpath "$shots")"
[[ -z "$shots" && -f "${demo%.ncdemo}.shots.json" ]] && shots="${demo%.ncdemo}.shots.json"
plugin_dll="${GAME_DIR:-/mnt/games/SteamLibrary/steamapps/common/Nuclear Option}/BepInEx/plugins/NuclearReplay/NuclearReplay.dll"

encoder="${ENCODER:-}"
if [[ -z "$encoder" ]]; then
    if ffmpeg -nostdin -hide_banner -v error -f lavfi -i color=black:s=256x256:d=0.1 -c:v h264_nvenc -f null - 2>/dev/null; then encoder=nvenc; else encoder=x264; fi
fi
if [[ "$encoder" == nvenc ]]; then
    venc=(-c:v h264_nvenc -preset p5 -tune hq -rc vbr -cq 19 -b:v 0 -g 60 -pix_fmt yuv420p)
else
    venc=(-c:v libx264 -preset veryfast -crf 16 -g 60 -pix_fmt yuv420p)
fi
# Encoders run at nice 10 while the machine is otherwise quiet, 19 when it is busy.
load=$(cut -d' ' -f1 /proc/loadavg); cores=$(nproc)
nice_level=$(python3 -c "print(10 if $load < $cores/2 else 19)")

# Plan: which requested ranges are cached, and which union segments must be rendered.
work="$(mktemp -d "${TMPDIR:-/tmp}/nc-render.XXXXXX")"
game_pid=""
tree() {   # tree <pid>: the pid and all its descendants
    local p="$1" c; echo "$p"
    for c in $(ps -e -o pid=,ppid= | awk -v p="$p" '$2 == p {print $1}'); do tree "$c"; done
}
stop_game() {   # stops only the game this script started (gamescope and everything under it)
    [[ -n "$game_pid" ]] || return 0
    local pids; pids=$(tree "$game_pid"); kill $pids 2>/dev/null || true; sleep 5; kill -9 $pids 2>/dev/null || true
}
cleanup() { stop_game; python3 -c "import shutil, sys; shutil.rmtree(sys.argv[1], ignore_errors=True)" "$work"; }
trap cleanup EXIT
python3 - "$demo" "$ranges" "$fps" "$width" "$height" "${shots:-}" "$plugin_dll" "$cache" "${NO_CACHE:-}" "$ff_speed" "$ff_step" "${venc[*]}" > "$work/plan.json" <<'PY'
import hashlib, json, os, sys
demo, ranges, fps, w, h, shots, dll, cache, nocache, ffs, ffstep, enc = sys.argv[1:]
req = []
for item in ranges.split(','):
    item = item.strip()
    if not item: continue
    span, _, f = item.partition('@')
    a, b = span.split(':')
    req.append({"name": f"clip_{a}-{b}.mp4", "from": float(a), "to": float(b), "fps": int(f or fps)})
req.sort(key=lambda r: r["from"])
shot_list = json.load(open(shots))["shots"] if shots else []
shot_list.sort(key=lambda s: s.get("t", 0))
st = os.stat(demo)
dll_hash = hashlib.sha256(open(dll, 'rb').read()).hexdigest()[:16] if os.path.exists(dll) else "none"
def key(r):
    # The camera in a range depends on the shot in effect at its start and the shots inside it.
    before = [s for s in shot_list if s.get("t", 0) <= r["from"]]
    inside = [s for s in shot_list if r["from"] < s.get("t", 0) <= r["to"]]
    blob = json.dumps({"demo": [os.path.basename(demo), st.st_size, int(st.st_mtime)], "from": r["from"], "to": r["to"],
                       "fps": r["fps"], "size": [w, h], "shots": before[-1:] + inside, "plugin": dll_hash,
                       "ff": [ffs, ffstep], "enc": enc, "v": 1}, sort_keys=True)
    return hashlib.sha256(blob.encode()).hexdigest()[:24]
todo = []
for r in req:
    r["key"] = key(r)
    r["cached"] = (not nocache) and os.path.exists(f"{cache}/{r['key']}.mp4")
    if not r["cached"]: todo.append(r)
# Union of overlapping (or nearly touching) ranges: the game can export only one range at a time.
segs = []
for r in todo:
    if segs and r["from"] < segs[-1]["to"] + 0.5:
        s = segs[-1]; s["to"] = max(s["to"], r["to"]); s["fps"] = max(s["fps"], r["fps"]); s["parts"].append(r)
    else:
        segs.append({"from": r["from"], "to": r["to"], "fps": r["fps"], "parts": [r]})
json.dump({"requests": req, "segments": segs}, sys.stdout)
PY

deliver() {   # deliver <src.mp4> <name> <key>: copy into outdir atomically and into the cache
    local src="$1" name="$2" key="$3"
    cp "$src" "$outdir/$name.part" && mv "$outdir/$name.part" "$outdir/$name"
    if [[ -z "${NO_CACHE:-}" ]]; then mkdir -p "$cache"; cp "$src" "$cache/$key.mp4.part" && mv "$cache/$key.mp4.part" "$cache/$key.mp4"; fi
    echo "$outdir/$name"
}

# Cached ranges land immediately.
while IFS=$'\t' read -r name key; do
    cp "$cache/$key.mp4" "$outdir/$name.part" && mv "$outdir/$name.part" "$outdir/$name"
    echo "$outdir/$name (cached)"
done < <(python3 -c "import json,sys; [print(r['name'], r['key'], sep='\t') for r in json.load(open(sys.argv[1]))['requests'] if r['cached']]" "$work/plan.json")

nseg=$(python3 -c "import json,sys; print(len(json.load(open(sys.argv[1]))['segments']))" "$work/plan.json")
[[ "$nseg" == 0 ]] && exit 0

# One FIFO per segment; the encoder loop reads them in order while the game writes.
frames="$work/frames"; mkdir -p "$frames"
python3 - "$work/plan.json" > "$work/segs.tsv" <<'PY'
import json, sys
p = json.load(open(sys.argv[1]))
for i, s in enumerate(p["segments"]):
    parts = ";".join(f"{r['name']}|{r['key']}|{r['from']}|{r['to']}|{r['fps']}" for r in s["parts"])
    num = lambda x: ("%.3f" % x).rstrip("0").rstrip(".")
    print(i, f"{num(s['from'])}:{num(s['to'])}@{s['fps']}", s["from"], s["fps"], parts, sep="\t")
PY
spec=""
while IFS=$'\t' read -r i seg _ _ _; do
    d="$frames/$(printf 'range_%02d' "$i")"; mkdir -p "$d"; mkfifo "$d/frames.rgba"
    spec+="${spec:+,}$seg"
done < "$work/segs.tsv"

nframes() { ffprobe -v error -select_streams v:0 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 "$1" 2>/dev/null || echo 0; }

split_and_deliver() {   # split_and_deliver <union.mp4> <segment start> <parts>
    local union="$1" sfrom="$2" parts="$3" p name key pfrom pto pfps n off
    IFS=';' read -ra plist <<<"$parts"
    for p in "${plist[@]}"; do
        IFS='|' read -r name key pfrom pto pfps <<<"$p"
        if [[ ${#plist[@]} == 1 ]]; then deliver "$union" "$name" "$key"; continue; fi
        # Part of a merged union: cut it out frame-exactly at its own rate.
        n=$(python3 -c "print(round(($pto-$pfrom)*$pfps))"); off=$(python3 -c "print(f'{$pfrom-$sfrom:.4f}')")
        nice -n "$nice_level" ffmpeg -nostdin -hide_banner -loglevel error -y -ss "$off" -i "$union" -frames:v "$n" -r "$pfps" "${venc[@]}" -f mp4 "$union.$name"
        deliver "$union.$name" "$name" "$key"
    done
}

encode_all() {
    local failed=0 i seg sfrom sfps parts d union jobs=()
    while IFS=$'\t' read -r i seg sfrom sfps parts; do
        d="$frames/$(printf 'range_%02d' "$i")"; union="$work/seg_$i.mp4"
        # Do not wait on a FIFO the game can no longer open.
        if [[ -e "$work/game_exited" && ! -e "$d.done" ]]; then echo "${seg%@*}: no frames exported" >&2; failed=1; continue; fi
        # Blocks until the game opens the FIFO; EOF when the range ends.
        nice -n "$nice_level" ffmpeg -nostdin -hide_banner -loglevel error -y -f rawvideo -pix_fmt rgba -s "${width}x${height}" -r "$sfps" \
            -i "$d/frames.rgba" "${venc[@]}" -f mp4 "$union" 2>"$work/enc_$i.log" || true
        if [[ ! -s "$union" || "$(nframes "$union")" -lt 1 ]]; then echo "${seg%@*}: no frames exported" >&2; failed=1; continue; fi
        split_and_deliver "$union" "$sfrom" "$parts" < /dev/null &   # the next range's encoder starts right away
        jobs+=($!)
    done < "$work/segs.tsv"
    for j in "${jobs[@]}"; do wait "$j" || failed=1; done
    return $failed
}
encode_all &
enc_pid=$!

# Generous watchdog: boot + skip at >= 2x + capture at <= 1x real time, plus slack.
last_to=$(python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['segments'][-1]['to'])" "$work/plan.json")
cap=$(python3 -c "import json,sys; print(sum(s['to']-s['from'] for s in json.load(open(sys.argv[1]))['segments']))" "$work/plan.json")
limit="${RENDER_TIMEOUT:-$(python3 -c "print(int(300 + $last_to/2 + 3*$cap))")}"
args=(-ncReplay "$demo" -ncNoHud -ncReplayFrames "$frames" "$fps" -ncCaptureRanges "$spec" -ncReplayQuit
      -ncFrameFormat raw -ncUncapped -ncFastForward "$ff_speed" "$ff_step" -ncPerfLog)
[[ -n "$shots" ]] && args+=(-ncShots "$shots")
"$here/run-headless.sh" "${args[@]}" >/dev/null 2>&1 &   # gamescope may segfault on exit; the encoders decide success
game_pid=$!
# run-headless.sh execs gamescope once it holds the game lock; the queue wait does not count against the watchdog.
while kill -0 "$game_pid" 2>/dev/null && [[ "$(ps -o comm= -p "$game_pid" 2>/dev/null)" != gamescope* ]]; do sleep 1; done
start=$SECONDS
while kill -0 "$game_pid" 2>/dev/null; do
    if (( SECONDS - start > limit )); then echo "render timed out after ${limit}s; stopping the game" >&2; stop_game; break; fi
    sleep 2
done
# gamescope --backend headless segfaults on exit after a normal run (harmless); keep the shell's notice quiet.
{ wait "$game_pid"; } 2>/dev/null || true
game_pid=""
# Release an encoder still blocked on a FIFO the game never opened: a read-write open never blocks,
# and closing it again hands the reader an EOF.
touch "$work/game_exited"
while kill -0 "$enc_pid" 2>/dev/null; do
    for f in "$frames"/range_*/frames.rgba; do { exec 3<>"$f"; exec 3>&-; } 2>/dev/null || true; done
    sleep 1
done
wait "$enc_pid"
