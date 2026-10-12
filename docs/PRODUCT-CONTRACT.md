# Product contract: plugin <-> nr

This file fixes the interfaces between the in-game plugins and the command line tool `nr` (`app/`).
Both sides code against it. Change it only together with both sides.

## 1. Event annotations inside `.ncdemo`

The recorder (NuclearReplay.dll, host only) writes gameplay events as annotation records
(record kind 2, UTF-8 JSON, same timestamps as message records). Replay time of a record is
`record.t - first_record.t`, exactly as `tools/demo_inspect.py` computes it, and is the time used
by shot lists (`"t"`) and render ranges.

Every event has `"event"` and may have more fields than listed. Readers must ignore unknown
events and unknown fields. A unit reference is:

```json
{"id": 169, "name": "Revoker", "type": "aircraft", "faction": "BDF", "player": "Talamander"}
```

`id` is the persistent unit id used by shot lists (`unit:<id>`). `player` is present only for
human-flown units. Any field except `id` may be missing.

| event | fields |
| --- | --- |
| `mission` | `name`, `map` |
| `player` | `name`, `faction`, `unit` (unit reference, when the player spawns an aircraft) |
| `launch` | `shooter`, `target` (or null), `weapon`, `missile` (unit id of the missile) |
| `kill` | `victim`, `killer` (or null), `weapon` (or null), `hit_ago` (optional, since 0.5.1) |

A `kill` record is written when the game's kill feed reports the death, which is often 2 to 3 s after
the hit (fire, breakup, crash). `hit_ago` is the number of seconds from the killer's last weapon hit on the
victim (missile impact, missile blast or gun hit) to the kill record, 0 to 20. It is missing when no weapon
hit by the killer was seen. Readers place the kill at `t - hit_ago` (the impact); without `hit_ago` the kill
stays at its record time. `nr` and the in-game viewer both do this.

Existing events (`ghost_authenticated`, `ghost_scene_ready`, `fault`, `end`) stay.

## 2. Render jobs: `-ncRenderJob <job.json>`

The replay plugin renders a job without any shell script and quits when done. It works on native
Windows and under Proton. `nr` writes the job file; all paths in it are paths the game process
can open (Windows paths; under Proton `nr` converts `/x/y` to `Z:\x\y`).

```json
{
  "version": 1,
  "demo": "C:\\...\\match.ncdemo",
  "width": 1920, "height": 1080,
  "shots": "C:\\...\\shots.json",
  "hud": false,
  "ffmpeg": "C:\\...\\ffmpeg.exe",
  "encoder": "auto",
  "quality": 19,
  "progress": "C:\\...\\progress.json",
  "ranges": [
    {"from": 1929.0, "to": 1952.0, "fps": 30, "out": "C:\\...\\Escalation 32-09 to 32-32.mp4"}
  ]
}
```

- `shots` may be null: the director then uses `<demo>.shots.json` or orbit/auto, as today.
- `encoder`: `auto` (try `h264_nvenc`, then `h264_vulkan`, then fall back to `libx264`), `nvenc`, `vulkan` or `x264`.
- The plugin starts `ffmpeg` itself and writes raw frames to its stdin. Each clip is written as
  `<out>.part` and renamed when complete.
- `progress` is rewritten (atomically) at least once per second:

```json
{"state": "loading|seeking|rendering|encoding|done|error",
 "range": 0, "ranges": 1, "frame": 120, "frames": 690, "error": null}
```

## 3. Watch: `-ncStart <seconds>`

`nr watch` starts the replay with `-ncReplay <demo> -ncStart <s>`: the
plugin skips ahead with the fixed-step fast-forward to `<s>` minus the preroll, then plays at 1x in a
normal game window with the usual replay keys.

## 4. Preview frames and thumbnails: `-ncPreview <job.json>`

`nr preview` (frames for scripts) and `nr thumbs` (moment pictures)
write a preview job and start the game with
`-ncPreview <job.json> -screen-fullscreen 0 -screen-width W -screen-height H`. The game runs muted in a
small window. With `"window": "offscreen"` (default) the plugin moves that window to -32000,-32000
without activating it (Windows API; under Proton this goes through Wine). Paths are game-side, as in §2.

```json
{
  "version": 1,
  "mode": "preview",
  "demo": "C:\\...\\match.ncdemo",
  "shots": "C:\\...\\shots.json",
  "width": 854, "height": 480, "fps": 20, "quality": 70,
  "hud": false, "mute": true, "window": "offscreen", "vflip": null, "preroll": 1,
  "progress": "C:\\...\\progress.json",
  "preview": {"port": 51234, "token": "<32 hex>", "from": 1924.0, "to": 1940.0,
              "autoplay": true, "softRestart": true}
}
```

- `mode`: `preview` streams frames over a socket; `thumbs` writes still pictures and quits.
- `vflip`: null lets the plugin flip rows when `SystemInfo.graphicsUVStartsAtTop` (D3D, DXVK).
  `true`/`false` force it.
- `fps`: 1 to 30. Size: up to 1920x1080; `nr preview` defaults to 854x480 at 20 fps.
- `progress` uses the §2 format (`state`, `range`, `ranges`, `frame`, `frames`, `error`) plus `mode` and
  `skipped`. In thumbs mode `frame`/`frames` count pictures; `skipped` counts pictures past the end of
  the recording.

### Thumbs mode

`"thumbs": [{"id": "event-12", "t": 1931.4, "out": "C:\\...\\event-12.jpg"}, ...]`, sorted by `t`.
The plugin skips ahead to each `t` in one run, renders at twice the job size, scales down, writes each
JPEG atomically and quits. The host caches pictures per recording under
`<data>/thumbs/<recording key>/` (`event-<index>.jpg`, `match.jpg`). The key includes the file name,
size and modified time.

### Preview socket

Preview mode only. The host is `nr preview` or a script. The host listens on `127.0.0.1:<port>`; the plugin connects. Every message, both ways
(`shared/Preview/PreviewWire.cs`, compiled into both sides):

| bytes | field |
| --- | --- |
| 1 | kind: `1` JSON message, `2` frame |
| 4 | header length, unsigned big-endian |
| 4 | payload length, unsigned big-endian (0 for JSON messages) |
| n | header: UTF-8 JSON object |
| m | payload: JPEG bytes for frames |

Plugin to host:

- `{"type":"hello","token":...,"mode":"preview","version":...}` first. The host drops connections with
  a wrong token.
- `{"type":"status","state":S,"t":..,"from":..,"to":..,"end":..,"fraction":..,"paused":..,"fps":..,"message":..,"error":..}`
  on every state change and about once a second. `S` is one of `starting`, `loading`, `seeking`,
  `ready`, `playing`, `paused`, `ended`, `restarting`, `error`. `fraction` (0..1) is sent while
  seeking. `end` is the recording length once loaded. Once frames flow, `grab_ms`, `encode_ms` and
  `game_fps` report the frame cost (averaged): main-thread capture time, worker-thread JPEG encode
  time, and the game's own frame rate.
- Frame messages (kind 2): header `{"type":"frame","t":..,"seq":..,"w":..,"h":..,"state":S}` plus a
  JPEG. While loading and seeking the plugin sends an occasional frame, so the host can show progress.
- `{"type":"error","message":..}` for a bad command; `{"type":"restart_needed","message":..}` when a
  command needs to go back in time and `softRestart` is false.

Host to plugin (JSON messages):

| command | effect |
| --- | --- |
| `{"type":"play"}` / `{"type":"pause"}` | Play or pause at 1x. `play` after `ended` restarts the range. |
| `{"type":"shots","shots":[...]}` | Replace the camera shot list live (shot list format of `nr shots`). |
| `{"type":"range","from":A,"to":B}` | New clip range. Forward: skip ahead in the running replay. Backward: reload the match (restart). |
| `{"type":"seek","t":T}` | Same as `range` from `T`, keeping the length when `T` is past the end. |
| `{"type":"restart","from":A,"to":B}` | Reload the match without restarting the game, then skip to `from`. |
| `{"type":"stream","fps":N}` | Change the frame rate, 1 to 30. |
| `{"type":"quit"}` | Quit the game. |

The replay only runs forward, so going back always reloads the match (status `restarting`, then
`loading` and `seeking` again). Hosts show it as a wait, not as an error. The game process quits when
the socket closes.

