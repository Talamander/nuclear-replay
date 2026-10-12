# Nuclear Replay for agents and scripts

Nuclear Replay records the Nuclear Option matches you host and renders any moment as an MP4 clip.
It is free. Players use the mod alone: the in-game viewer watches replays and records clips. The
command line tool `nr` drives the same mod from scripts and agents: it lists recordings and moments,
installs the mod, opens a replay in the game and renders clips headless. `nr` is not in the release;
build it from source (`docs/DEVELOPMENT.md`).

Developers: see `app/AGENTS.md` for the code layout and rules. The contract with the in-game plugin
(demo files, job files, shot lists, progress) is `docs/PRODUCT-CONTRACT.md`.

## Quick start

```
nr status --json                          # game found? mod installed? ffmpeg present? fixes for each problem
nr list --json                            # recordings, newest first
nr events latest --moments --json         # launches and kills, each with an index
nr render latest --event 12 --style victim_cut --size vertical --json
```

`render` writes a job file, starts the game through Steam (`-applaunch 2168680 -ncRenderJob <job>`),
and follows `progress.json` until the plugin reports `done` or `error`. The game opens by itself,
films the clip and closes. Do not render while someone is playing: the game takes the screen and GPU,
and render refuses to start while Nuclear Option is running.

## Building a clip command

`nr render` takes the moment, camera style, format, frame rate, HUD, encoder, quality and file name
as options. Options left at their defaults can be left out. `nr shots make` writes the camera list
for a clip to a JSON file and prints the matching `nr render ... --range A:B --shots FILE` line. Edit
the cameras by hand or with an agent, then run it.

## JSON output

Every command takes `--json` and prints one result document on stdout:

```json
{ "ok": true,  "command": "list", "data": { ... } }
{ "ok": false, "command": "render", "error": { "code": "game_running", "message": "...", "fix": "..." } }
```

`error.fix` is a plain-English next step. Show it to the user or act on it.

Long commands (`render` without `--no-wait`) first print NDJSON progress lines, one compact object
per line, each with `"type":"progress"`. The result document comes last and is indented. To parse:
drop lines whose JSON has `type == "progress"`, then parse the rest as one document.

Exit codes: `0` success, `1` the operation failed (game missing, launch failed, render failed),
`2` the input was invalid (bad shot list, unknown recording, bad range or size).

The JSON is stable: fields get added, never renamed or removed.

## Commands

```
nr status                         game, BepInEx, plugins, Proton override, ffmpeg, recordings
nr install [--from DIR] [--bepinex ZIP] [--no-proton-fix] [--no-ffmpeg] [--steps]
                                  install or update BepInEx 5 and the mod; close the game first.
                                  Then downloads ffmpeg when there is none (--no-ffmpeg skips it).
                                  Also gives the in-game viewer the ffmpeg and clips folder (see
                                  "The in-game replay viewer"). data.summary lists what was done in
                                  plain sentences; --steps also prints every file written or kept.
                                  data.ffmpeg.action is installed, kept, missing, skipped or failed;
                                  a failed download does not fail the install (data.ffmpeg.error)
nr ffmpeg status                  which ffmpeg is used (path, source: setting|shared|bundled) and
                                  whether the pinned build is in <game>/NuclearReplay/tools/ffmpeg
nr ffmpeg install [--force]       download the pinned ffmpeg build there (see "Video encoder")
nr list [--dir DIR]               recordings, newest first: map, date, length, players,
                                  launches, kills, missile_hits
nr events <demo> [--moments]      header plus timeline; --moments keeps only launches and kills
nr presets                        camera styles, sizes, shot cameras and targets
nr shots schema                   JSON Schema for shot lists
nr shots validate <file> [--demo <demo>]     exit 2 when the list has errors
nr shots make <demo> (--event N | --at SECONDS | --range A:B) [--style S] [--pre 5] [--post 6] [--out FILE]
nr render <demo> (--event N | --at SECONDS | --range A:B[,C:D]) [--pre 5] [--post 6]
          [--shots FILE | --style S] [--size 1080p|vertical|4k|WxH] [--fps 60]
          [--out DIR] [--hud] [--encoder auto|nvenc|vulkan|x264] [--quality 19] [--name PREFIX]
          [--dry-run] [--no-wait]
nr open <demo> [--at SECONDS] [--focus SECONDS] [--dry-run]
                                  opens the recording in the in-game replay viewer (visible game
                                  window); clips recorded there go to the clips folder
nr watch <demo> [--at SECONDS | --event N [--pre 5]] [--shots FILE] [--dry-run]
                                  plays the replay in the game with a fixed shot list (no viewer)
nr preview <demo> (--event N | --at SECONDS | --range A:B) [--pre 5] [--post 6]
          [--style S | --shots FILE] [--size 854x480] [--fps 20] [--quality 70] [--hud]
          [--out DIR] [--latest-only] [--mjpeg PORT] [--max-frames N] [--seek-frames]
          [--commands [--paused]] [--show-window] [--simulate [--simulate-speed 10]] [--dry-run]
                                  plays part of a recording in the game in the background and saves
                                  small JPEG frames (for scripts). See
                                  "Preview frames" below
nr thumbs <demo> [--event N ...] [--no-match] [--all] [--list] [--size 320x180]
          [--show-window] [--dry-run] [--no-wait] [--simulate [--simulate-speed 10]]
                                  still pictures of moments and the match card, cached per recording
nr diagnostics [--out FILE]       one text file for bug reports: status, settings, nr log,
                                  the end of BepInEx/LogOutput.log, the last render job
nr settings                       show settings
nr settings set <key> <value>     game_dir, output_dir, demo_dir, ffmpeg_path, default_fps, ...
```

Global options: `--json` and `--game-dir DIR` (for this command only, not saved).

`<demo>` is a path, a file name from `nr list` (with or without `.ncdemo`), part of a name, or `latest`.

## Picking the moment

- `--event N`: the index from `nr events --moments` (a launch or a kill). The clip runs from
  `--pre` seconds before it to `--post` seconds after it (defaults 5 and 6).
- `--at SECONDS`: the same, around a replay second. Use it for recordings without events.
- `--range A:B[,C:D]`: replay seconds, one clip per range. Ranges must not overlap (touching ranges
  such as `10:20,20:30` are fine). Ranges past the end of the recording are an input error (exit 2).

Replay seconds count from the first record in the demo. `nr events --json` gives each moment its
`index`, `t`, `camera_target` (`unit:<id>`) and, for launches, `missile_target`. A kill's `t` is the moment
the killer's weapon hit the victim when the recording has it (recorder 0.5.1 and later); those kills also carry
`kill_feed_t`, the game's kill feed message, which comes when the victim dies, often 2 to 3 s later. Kills in
older recordings, and kills with no weapon hit on record, have only the kill feed time in `t`.

A kill "pairs" with a missile launch when the same aircraft fired at that target with a guided
missile up to 90 s before. `missile_hits` in `nr list` counts paired kills. The missile camera styles
use the pair: on a paired kill they start on the shooter, ride the missile and end on the target.

## Camera styles

| Style | What it does |
| --- | --- |
| `cinematic_chase` | Follows the shooter, then rides the missile to the hit. Default. |
| `victim_cut` | Rides the missile, then cuts to the target going down at impact. |
| `missile_follow` | Stays on the missile from launch to impact, then holds on the hit. |
| `chase` | Behind the aircraft, steady. |
| `orbit` | Circles the aircraft (the victim on a kill). |
| `flyby` | A spotter camera watching the action pass by. |
| `cockpit` | From the pilot's seat. |

`nr presets --json` lists them with the sizes, shot cameras and targets.

## Shot lists

```json
{ "shots": [
  { "t": 11.44,  "camera": "chase", "target": "unit:128", "fov": 50 },
  { "t": 16.844, "camera": "chase", "target": "unit:139", "fov": 60, "hold": 3 }
] }
```

- `t`: replay seconds. Required. Each shot runs until the next one.
- `camera`: `orbit`, `chase`, `cockpit`, `tv`, `free`, `relative`, or the cinematic `chase_cine`. Default `orbit`.
- `target`: `host`, `auto`, `latest_missile`, `latest_host_missile`, `unit:<id>`, `name:<text>`. Default `host`.
- `fov` in degrees and `hold` in seconds are optional. `hold` is how long the camera stays on a destroyed target before it cuts to the nearest live aircraft (default 3); a detonated missile holds a still view of the explosion.
- `then`: optional shot object (`camera`, `target`, `fov`, `hold`) to cut to once the target is gone and the hold is over, instead of the nearest aircraft. `victim_cut` uses `"hold": 0` with `"then"` on the victim, so the cut follows the missile even when the recording has only the kill feed time, which can be seconds after the hit.

Workflow for a custom cut:

```
nr shots make latest --event 12 --style victim_cut --out shots.json --json   # data.range is the clip range
# edit shots.json
nr shots validate shots.json --demo latest --json
nr render latest --range 35:51 --shots shots.json --json
```

## Render jobs

`nr render --dry-run --json` writes the job file and returns its path and the launch command without
starting the game. The job (`render-job.schema.json`) holds the demo path, size, shot list, HUD flag,
ffmpeg path, encoder, quality, the progress file path and one entry per range (`from`, `to`, `fps`,
`out`). Agents may write job files themselves and start the game with `-ncRenderJob <job>`; follow the
progress file the job names. `nr render --no-wait` does the launch and returns at once.

A failed render's `fix` says what to try and names the game's mod log (`<game>/BepInEx/LogOutput.log`)
and the job folder.

## The in-game replay viewer

`nr open <demo>` starts Nuclear Option in its normal, visible window with the replay viewer. The
player watches the match in the game at full quality, with sound, and records clips there. The game's
own **Replays** button in the main menu opens the same viewer without `nr`.

```
nr open latest --json                           # from the start
nr open latest --at 35 --focus 40 --json        # start at 0:35, focus the moment at 0:40
```

- `--at SECONDS` starts the viewer at that replay second (it skips ahead, then plays).
- `--focus SECONDS` selects the moment at that replay second and sets the clip in and out points
  around it. To open a moment, pass the clip start as `--at` and the moment time as `--focus`.
- The result has `demo`, `launch`, `dry_run`, `arguments`, `at`, `focus` and `clips_dir`.
  `--dry-run` prints the launch without starting the game. Without it, `nr open` fails with
  `game_running` while Nuclear Option is open.
- The game arguments are `-ncReplay <demo> -ncViewer [-ncAt S] [-ncFocus S] [-ncClipsDir <dir>]
  [-ncFfmpeg <ffmpeg>] [-ncQuality N] [-ncEncoder auto|nvenc|vulkan|x264]`, with game-side paths
  (Proton `Z:\...` on Linux) and seconds in invariant culture. The last three come from the settings
  (`ffmpeg_path` or the downloaded ffmpeg, `quality`, `encoder`); the plugin keeps them and the clips
  folder in its config, so launches from the game's own Replays menu use them too.
- `nr install` writes the same ffmpeg and clips
  folder into the replay plugin's config, `<game>/BepInEx/config/talamander.nuclearreplay.player.cfg`,
  section `[Viewer]`, keys `FfmpegPath` and `ClipsDirectory`, as game-side paths. It fills only keys
  that are missing or empty and never changes a value that is already set. `FfmpegPath` is the
  `ffmpeg_path` setting when that file exists, else the shared location
  `<game>/NuclearReplay/tools/ffmpeg/ffmpeg.exe`, even before the download. When only the config of an older version exists
  (`local.nuclearoption.context.replay.cfg`), it copies that to the new name first and fills the
  copy. So "Clip this" works in a game started from Steam right after the install.

Keys in the viewer:

| Key | Action |
| --- | --- |
| Space | Play or pause |
| Left / Right | Skip back or forward 10 seconds |
| Up / Down, or + / - | Playback speed, 0.25x to 4x |
| 1 to 7 | Camera presets: cinematic chase, victim cut, missile follow, chase, orbit, flyby, cockpit |
| [ and ] | Previous or next unit |
| F | Free camera: WASD to move, Q/E down and up, Shift to go faster, hold the right mouse button to look |
| I / O | Set the clip in and out points |
| C | Record the clip: it plays at 1x in real time and is encoded at screen resolution, with the GPU encoder when one is available. C or Esc while recording stops it |
| H | Hide all UI |
| G | Show or hide the game's own HUD |
| F1 | Help panel |
| Backspace | Leave to the main menu |

The game's main menu also gets a **Replays** button that lists the recordings.

The viewer looks for ffmpeg in this order: `-ncFfmpeg`, the config `FfmpegPath` (when the file
exists), then `<game>/NuclearReplay/tools/ffmpeg/ffmpeg.exe`. When none exists, C asks first: "Clips
need ffmpeg, a free video tool, about 258 MB from github.com. Download now?" (Y/Enter yes, N/Esc
no). Yes downloads the same pinned build `nr` uses into that shared folder, with progress in the
overlay, then checks the SHA-256 and unpacks it off the game thread. Nothing is downloaded without
a yes.

Encoders: `auto` tries `h264_nvenc`, then `h264_vulkan`, then `libx264`. Under Proton, `vulkan`
encodes on the GPU where NVENC does not work.

Clips recorded with C are saved to `[Viewer] ClipsDirectory` (set by `-ncClipsDir` or `nr install`).
When it is empty they go to `<game>/NuclearReplay/clips`. File names are `<mission> m-ss to m-ss.mp4`
in whole replay seconds, with ` (2)`, ` (3)` and up when the name is taken
(`shared/Clips/ClipNames.cs`, the same scheme `nr render` uses).

The plugin also writes one folder per clip under
`<game>/NuclearReplay/viewer-clips/<yyyyMMddTHHmmssZ>-<6 hex>/` with a `job.json` (the render job
format plus `"source":"viewer"`, `camera_style` and `mission`) and a `progress.json` (`state` is
`recording`, `encoding`, `done` or `error`). The desktop app used to import these; nothing in this
repository reads them now, but scripts can.

## Preview frames

`nr preview` plays a clip range in the game in the background (muted, window moved off-screen unless
`--show-window`) and saves small JPEG frames: `latest.jpg` and `frame-NNNNNN.jpg` in `--out` (default:
`frames/` in the job folder; `--latest-only` keeps only `latest.jpg`). `--mjpeg PORT` also serves the
frames on `http://127.0.0.1:<port>/` (`0` picks a free port), with the newest frame at `/latest.jpg`.
`--commands` reads JSON commands from stdin, one per line (`play`, `pause`, `shots`, `range`, `seek`,
`restart`, `stream`, `quit`; `docs/PRODUCT-CONTRACT.md` section 4 in the source), and runs until
`quit`, end of input or an error. Without `--commands` it stops when the range ends.

The result has `stopped_by` (`ended`, `max_frames`, `quit`, `end_of_input`, `cancelled`, or the
plugin's final state), `frames`, `out_dir`, `latest`, `job_dir`, `job_file`, `launch`, `status` and,
with `--mjpeg`, `mjpeg` (the URL). `--simulate` uses the built-in stand-in for the game (see below).

## Moment pictures

`nr thumbs <demo>` takes one picture per launch and kill (a kill orbits the victim 0.4 s after the hit,
a launch chases the shooter 0.6 s after firing, with a level horizon) and one for the match card, in one background game run, and caches them under `<data>/thumbs/<recording key>/` as
`event-<index>.jpg` and `match.jpg`. Pictures already cached are skipped unless `--all`. `--list`
only reports the cache and never starts the game. The result lists `planned`, `missing` and each
picture's `out` path. `--no-wait` starts the game and returns; follow `progress.json` in the job folder.

`--simulate` uses a built-in stand-in for the game. It draws labelled test pictures marked "not game
footage". Simulated pictures go to a separate cache (`<data>/thumbs-simulated`). Use it to test
scripts without starting Nuclear Option.

Only one thing uses the game at a time. `nr preview`, `nr thumbs`, `nr render` and `nr open` fail
with `game_running` while Nuclear Option is open.

## Video encoder (ffmpeg)

Clips and renders need ffmpeg. The release zip does not include it. The game and `nr` use
one copy at `<game>/NuclearReplay/tools/ffmpeg/ffmpeg.exe` (plus `LICENSE.txt`), downloaded on
request from a pinned URL and checked against a pinned size and SHA-256 before it is unpacked. The
pin lives in `shared/Ffmpeg/FfmpegPackage.cs`; `nr ffmpeg status` prints it under `data.pinned`.
It is the Windows build on Linux too, because the game runs under Proton.

`nr` resolves ffmpeg in this order: the `ffmpeg_path` setting, the shared location, then
`ffmpeg/ffmpeg.exe` in the bundled folder (development builds only). `nr ffmpeg install` prints
NDJSON progress lines (`"type":"progress"`, `state` `ffmpeg`, `stage` `download`, `verify` or
`unpack`, `received`, `total`, `fraction`) and leaves no partial file behind when it fails.

```
nr ffmpeg status --json        # data.present, data.path, data.source, data.matches_pin
nr ffmpeg install --json       # download, verify, unpack; does nothing when already there
```

## Common error codes

| Code | Meaning and fix |
| --- | --- |
| `game_not_found` | Steam has no Nuclear Option install. Pass `--game-dir` or `nr settings set game_dir ...`. |
| `game_running` | Close Nuclear Option, then try again. Install, render and open refuse while it runs (`--dry-run` does not). |
| `ffmpeg_missing` | No video encoder yet. Run `nr ffmpeg install`, or set `ffmpeg_path` to a Windows `ffmpeg.exe` (also on Linux: the game runs under Proton). |
| `ffmpeg_download_failed` | The download did not finish (offline, HTTP error, stalled for 60 s). Nothing was kept; try again. |
| `ffmpeg_hash_mismatch` | The download has the wrong size or SHA-256. Nothing was installed; try again. |
| `ffmpeg_install_failed` | The game folder is not writable, or `ffmpeg.exe` is in use. Close the game and any render, then try again. |
| `install_failed` | The mod could not be installed. The `fix` says why; nothing is left half-written. |
| `launch_failed` | Steam did not start the game. Start Steam and sign in. |
| `render_failed` | The plugin reported an error. The `fix` says what to try and names the mod log and the job folder. |
| `thumbs_failed` | The plugin could not make the pictures. The `fix` names the mod log and the job folder. |
| `preview_failed` | The preview stopped with an error or the game closed before it started. The `fix` says what to try. |
| `no_thumbs` | Nothing to take pictures of (no moments, or every `--event` is invalid). |
| `demo_not_found`, `bad_event`, `bad_range`, `bad_start`, `bad_size`, `bad_fps`, `bad_style`, `bad_encoder`, `invalid_shots`, `bad_quality`, `bad_speed`, `bad_port`, `bad_max_frames`, `bad_key`, `bad_value` | Input errors (exit 2). The `fix` says what to change. |

`nr status` does not fail on a missing BepInEx or plugin; it lists each check with `ok` and a `fix`
(usually "run `nr install`"). Check `ready_to_render` before rendering.

## State and environment

- Settings (`settings.json`) live in `%APPDATA%\Nuclear Replay` on Windows and
  `~/.config/nuclear-replay` on Linux. Job files, logs and install backups live in `%LOCALAPPDATA%\Nuclear Replay` or `~/.local/share/nuclear-replay`.
- `NC_HOME=<dir>` puts all state under one folder (`<dir>/config`, `<dir>/data`). Use it for
  isolated runs.
- `NC_BUNDLED_DIR=<dir>` points to the plugin and BepInEx zip `nr install` uses. Without it `nr`
  uses `bundled/` next to the executable (`app/bundled/README.md`).
- `NC_LAUNCH_WRAPPER=<program>` (testing): for renders and pictures, `nr` runs
  `<program> <game arguments>` instead of `steam -applaunch`, and stopping the game kills only the
  process it started. Viewer launches (`nr open`) ignore it.
- `NC_VIEWER_WRAPPER=<program>` (testing): viewer launches (`nr open`) run `<program> <game arguments>` instead of Steam or the exe. Unset, they always go through
  Steam (the exe on Windows), even when `NC_LAUNCH_WRAPPER` is set. The value `headless` uses
  `NC_LAUNCH_WRAPPER` instead. `nr` never closes a viewer game.
- Log: `<data>/logs/nuclear-replay.log`, written by `nr`. The game's mod log is
  `<game>/BepInEx/LogOutput.log`.
- Recordings: `<game>/NuclearReplay/demos/*.ncdemo`. Only the host records.

## Historical names

The mod was called Nuclear Context before 0.5.0. Some names from that time stay on purpose, because
renaming them would break recordings, scripts or older installs:

- The demo file extension `.ncdemo` and the format id `nuclear-context-network-demo` in its header.
- The game arguments `-nc*` (`-ncReplay`, `-ncRenderJob`, `-ncFfmpeg` and the others).
- The environment variables `NC_*` (`NC_HOME`, `NC_BUNDLED_DIR`, `NC_LAUNCH_WRAPPER`, ...).

Old installs are read, never moved or deleted:

- `<game>/NuclearContext/demos` is listed next to `<game>/NuclearReplay/demos` (by `nr list` and
  the in-game Replays menu). `nr list --json` names it in `data.extra_dirs`. New recordings go
  only to `<game>/NuclearReplay/demos`.
- `<game>/NuclearContext/tools/ffmpeg/ffmpeg.exe` is used when the new location has no ffmpeg.
- On its first start each plugin copies the newest old config file (`talamander.nuclearcontext*.cfg`,
  then `local.nuclearoption.context*.cfg`) to `talamander.nuclearreplay.recorder.cfg` or
  `talamander.nuclearreplay.player.cfg` when the new file does not exist yet.
- `nr` copies `settings.json` once from the old `Nuclear Context` / `nuclear-context` state folders.
- `nr install` moves an old `BepInEx/plugins/NuclearContext` folder into the install
  backup. When BepInEx still loads an old plugin, the recorder stays off for that session and the
  game shows a warning.
- The command line tool was called `nc` before 0.5.0; it is `nr` now.
