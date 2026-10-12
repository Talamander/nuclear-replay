# Developer notes and history

## Start here

The player-facing README is [../README.md](../README.md). These are the developer documents:

- [PRODUCT-STATUS.md](PRODUCT-STATUS.md): what is done, and the release checklist.
- [PRODUCT-CONTRACT.md](PRODUCT-CONTRACT.md): demo events, render jobs and the game arguments shared by the
  plugins and `nr`.
- [../app/AGENTS.md](../app/AGENTS.md): code layout of `nr` and `NuclearReplay.Core`.
- [../app/RELEASING.md](../app/RELEASING.md): making a release.
- [TESTING.md](TESTING.md): tests, including against an isolated game copy.
- [../DEMO.md](../DEMO.md): the network demo recorder and replay plugin in detail.
- [../tools/readme-shots/README.md](../tools/readme-shots/README.md): how the README screenshots and GIF in
  `docs/media/` were made.

### Build the mod

Building needs the .NET 10 SDK and a local Nuclear Option install with BepInEx, because the plugins compile
against the game's own assemblies. No game files are in this repository or in the release.

```sh
scripts/build.sh '/path/to/steamapps/common/Nuclear Option'      # builds dist/NuclearReplay.dll
scripts/install.sh '/path/to/steamapps/common/Nuclear Option'    # copies it into BepInEx/plugins/NuclearReplay
GAME_DIR='/path/to/steamapps/common/Nuclear Option' scripts/package.sh   # the release zip
```

`scripts/package.sh` writes `dist/NuclearReplay-<version>-plugins.zip` (the DLL, `LICENSE.txt` and
`README.txt`) and `dist/SHA256SUMS`. FFmpeg is not in the zip; the game downloads the build pinned in
`shared/Ffmpeg/FfmpegPackage.cs` when the player first makes a clip.

### `nr`, the command line tool

`nr` lists recordings and their moments, installs the mod, opens a replay in the game, and renders clips
headless, all with JSON output for scripts and agents. It is not part of the release; build it from source:

```sh
cd app
dotnet build NuclearReplay.slnx
dotnet run --project src/NuclearReplay.Cli -- status
dotnet publish src/NuclearReplay.Cli -c Release -r linux-x64 -o ~/.local/share/nr   # or -r win-x64
```

The publish step makes one self-contained executable, `nr` (`nr.exe` on Windows). `nr install` needs the
plugin and a BepInEx zip: pass `--from <folder with NuclearReplay.dll> --bepinex <BepInEx_win_x64_5.4.23.5.zip>`,
or put them in a `bundled/` folder next to `nr` (`app/bundled/README.md`). [AGENTS.md](AGENTS.md) explains the
commands, shot lists and render jobs; `app/AGENTS.md` describes the code.

## History

This is the developer README from before the first release. It describes the recorder's telemetry
bundle, a short history of the research toward cinematic playback and how to build and test the plugins by hand. For
the product (in-game viewer, `nr`, packaging) start with `docs/PRODUCT-STATUS.md`,
`docs/AGENTS.md`, `app/AGENTS.md` and `docs/TESTING.md`. Paths below are from the owner's machine.

A small observer plugin for Nuclear Option that writes gameplay facts beside a video recording. It does not generate narration, send data to a service, or change simulation inputs.

**Current status (2026-10-01): network demo record and replay work.** Recorder 0.4.0 records every hosted mission as a network demo. Replay plugin 0.1.0 plays a demo inside the real game, with the game's own visuals, effects and outcomes, from programmable cameras: orbit, chase, cockpit, missile follow and automatic cuts. It can also render clips off-screen to MP4. See [DEMO.md](../DEMO.md) for usage and limits. The sections below describe the optional telemetry bundle the recorder can still write beside demos (off on a fresh install; configs from older versions keep it on) and how it was first tested.

**Earlier status:** recorders 0.1 to 0.3 explored per-frame visual capture and an offline in-engine playback experiment (playback core, camera director, host/client pairing, native mesh and particle restoration). Visual capture caused severe lag in its one live test (median 253.6 ms per captured frame) and was dropped on 2026-10-01 for network demo record and replay. Its code, tests, tools and records (the playback projects, the visual-capture tests, `HOOKS.md`, `RECONSTRUCTION.md`, `VALIDATION.md`, `CONTINUE-CINEMATICS.md`) were moved out of this repository into a separate research archive before the public release. [HOOKS.md](HOOKS.md) keeps the hook list that still describes the recorder.

## Repository and continuation

Git tracks authored source, tests, scripts and documentation. The private GitHub repository is a source backup, not a backup of recordings or the installed game. Build outputs, local research/decompilations, third-party source checkouts, capture bundles and media are excluded. Building requires the locally installed game/loader assemblies described below. No third-party source or game assets are redistributed.

## Local installation inspected

- Nuclear Option 0.34.1, Steam build 24724372, Unity 2022.3.62f2, Mono.
- Omarchy Linux, Steam app 2168680, Proton `experimental-11.0-20260924-x86_64`.
- Game: `/mnt/games/SteamLibrary/steamapps/common/Nuclear Option`.
- Existing BepInEx 5.4.23.5, HarmonyX assembly 2.9.0.0/product 2.9.0.
- Existing NOVR 0.4.4, AB6/MTQ bindings, NOFFB, NOVR MCP bridge, HUD and tree fixes.
- OBS 32.2.2, Tutorial profile, 1920×1080 at 30 FPS. OBS WebSocket server is disabled, so this version uses visible markers. It does not change recorder settings.
- OBS videos: `/run/media/talamander/T7/Gameplay Recordings`.

The plugin references the actual installed game and loader DLLs. It does not ship those DLLs. Actual spawn context requires a local single-player/host observer. Client observations record replicated initialization and subsequent local state; they do not claim exact launch time, original target assignment, or launch range.

## Build, install, remove

Requirements: a .NET SDK that supports netstandard2.1 (SDK 10.0.111 was used locally), the installed game and BepInEx. The command-line editor tool needs Python 3 and ffmpeg/ffprobe; it uses no Python packages.

```sh
GAME_DIR='/mnt/games/SteamLibrary/steamapps/common/Nuclear Option'
./scripts/build.sh "$GAME_DIR"
./scripts/install.sh "$GAME_DIR"
```

Installation places only `NuclearReplay.dll` in `BepInEx/plugins/NuclearReplay/`. Replacing the file does not reload a running plugin. Start the game normally when you finish the current capture; no restart is required during that capture. The installer saves any previous plugin under this project's `dist/NuclearReplay.previous.dll`.

To remove, close the game normally, then delete `BepInEx/plugins/NuclearReplay/NuclearReplay.dll`. Saved bundles and `BepInEx/config/talamander.nuclearreplay.recorder.cfg` (before 0.5.0, `talamander.nuclearcontext.cfg` or `local.nuclearoption.context.cfg`) remain available. No game assemblies, control maps, or NOVR files require restoration.

## Capture after returning to the PC

1. Use a local single-player/host mission. Start OBS recording and confirm its preview shows the game mirror.
2. With the game focused, press **F8**. Confirm the yellow `NC-…` marker appears in the actual recording. It lasts two real seconds. Its render event alone is not proof of capture.
3. Select a target, launch a missile, change target, then launch another missile. Let their observable outcomes occur. Pause/resume the game briefly if practical.
4. Press **F8** again before stopping OBS. Keep OBS recording continuously between these two markers.
5. Stop OBS. Close the game normally to drain the writer before final validation. If you keep playing, the files remain live and may end with a partially flushed JSONL line; do not run final validation yet.

For an OBS recording pause, place markers around **each uninterrupted active interval**, including after resuming. A pause changes the video offset. Use distinct segment names. Stops/restarts create separate files and segments. Game pauses do not change video alignment because telemetry uses a monotonic real clock.

Configuration lives in `BepInEx/config/talamander.nuclearreplay.recorder.cfg` (`talamander.nuclearcontext.cfg` or `local.nuclearoption.context.cfg` before 0.5.0; the plugin copies the newest one over once): `Capture.SampleHz` defaults to 5 and clamps to 1–30; `Capture.QueueCapacity` defaults to 4096; `Video.SyncKey` defaults to F8 in 0.1.1; `Capture.OutputDirectory` defaults to `<game>/NuclearReplay`. Under Proton, use paths accessible through Wine drive mappings. The marker is a screen overlay for the game mirror; its visibility inside a headset is not established.

The first 0.1.0 session uses F9, which this desktop reserves for dictation. The next-start configuration uses F8. On this Lua-based Hyprland installation, `python3 tools/sync.py --key F9` delivers a marker directly to the focused game without changing desktop bindings; use `--key F8` with the next-start configuration. This path has produced a marker verified in the actual OBS video. Always match the key to the running plugin, and do not treat successful key delivery as synchronization proof.

## Bind video and export

Each game process creates a directory under `<game>/NuclearReplay/<UTC>-<session-prefix>/`. Set the actual paths, then list markers:

```sh
BUNDLE='/path/to/game/NuclearReplay/your-session'
VIDEO='/path/to/your-recording.mp4'
python3 tools/context.py markers "$BUNDLE"
```

Find the **first video frame** displaying each matching marker. Use a video player with frame stepping, or extract a frame at a candidate timestamp:

```sh
python3 tools/context.py frame "$VIDEO" --time 12.033333 --output marker.png
```

The values below are examples, not measured timestamps or real markers. Replace them with reviewed values from your capture:

```sh
python3 tools/context.py align "$VIDEO" "$BUNDLE" \
  --anchor NC-SESSION-001=12.033333 \
  --anchor NC-SESSION-002=95.100000 \
  --segment recording-1 --uncertainty 0.1 --verified
python3 tools/context.py export "$VIDEO" "$BUNDLE"
python3 tools/context.py query "$VIDEO" "$BUNDLE" --start 20 --end 40 --states
python3 tools/context.py validate "$BUNDLE" --strict
```

`--uncertainty` is the operator's timestamp/presentation uncertainty in seconds, not an automatic guarantee. The tool adds video frame duration, game frame duration, and measured anchor residual. It stores video filename, SHA-256, anchor event IDs, offset, coverage and uncertainty in `session.json`. It reports residual consistency; absolute capture presentation latency remains unknown. It rejects conflicting anchors rather than fitting a time scale that hides recorder pauses. It maps only the interval bounded by anchors. Every other video time stays unknown.

Repeat `align` with distinct `--segment` values for recording pauses/restarts. Segments must not overlap in telemetry time. Query output is JSONL, retains full raw facts, and includes supporting launch events when an outcome's launch occurred before the requested range.

## Bundle facts

- `session.json`: schema 1.1 (the editor also reads 1.0), session/game/loader/mod versions, game assembly hash, units, hook installation status, limits and recording alignment.
- `events.jsonl`: discrete events with event, session, mission, entity and projectile IDs where applicable.
- `states.jsonl`: player/selection/warning context at 5 Hz; world motion at 10 Hz; missile motion at 20 Hz; mission/environment and capture health at 1 Hz. These are requested rates, limited by frame rate and capture capacity. Each motion pose carries its actual sample time.
- `timeline.md`: generated by `export`, chronological summaries with video timestamps and supporting event IDs. Until export it contains an alignment notice.
- `writer-status.json`: periodically flushed and final queue statistics, dropped events/states, writer fault and clean shutdown status.

All Unity reads occur on the game thread. Serialized immutable strings pass through a bounded queue to a background writer, which flushes about once per real second. The plugin bootstraps the existing `UnitRegistry.allUnits` once per mission and maintains its own registry through lifecycle hooks. Motion sampling reads that registry; it does not repeatedly search Unity objects. Queued records can be lost on forced termination; inspect `writer-status.json` and `hook_error` events before trusting a capture.

On the host, launch context freezes the actual `Spawner.SpawnMissile` target argument, launching-unit/target positions, local-player selection when applicable, real/UTC/game timestamps, altitude, speed and both slant/horizontal ranges at invocation. The returned Missile receives a session/mission identity that links subsequent observations. Selected target, spawn assignment and later simulation seeker changes remain separate facts. No target or hit is reconstructed from later selection.

Position is global Unity mission coordinates in metres (local transform position minus `Datum.originPosition`). Altitude is global y above sea level zero. Speed is `Unit.rb.velocity` magnitude in m/s, not cockpit indicated airspeed. Heading is projected forward with mission +z = 0°, not asserted geographic north. Ranges use launching-unit and assigned-target origins observed on the host; remote aircraft may be client-authoritative. No simultaneous remote physical state is promised. Projectile launch position is separate. Part health uses game part health units, not aggregate aircraft percentage. Exact identity, position, damage and seeker data may be hidden from the player; `player_recognized_type` remains null.

Detonation, collision, applied part health change, dealer credit, destruction report and removal are different events. Only an explicit missile ID or nested synchronous damage scope links projectile damage. Disappearance does not establish a hit. Destruction does not establish an exclusive player kill. Delayed or asynchronous effects without explicit projectile identity remain unlinked.

## Tests

```sh
dotnet run --project tests/CoreTests.csproj
dotnet run --project tests/Contracts.csproj
dotnet run --project tests/DemoFileTests.csproj
dotnet run --project tests/ConfigMigrationTests.csproj
python3 -m unittest discover -s tests -p 'test_*.py' -v
```

Tests use clearly labeled synthetic facts. They check immutable serialization, bounded queues and disk failure, independent missile identities, mission boundaries, target changes, respawns, pause/segment mappings, unknown outcomes, distance corruption and range-query evidence. They do not establish that a live game hook or video marker works.

See [HOOKS.md](HOOKS.md) for the hook list and upstream investigation. No code from NOBlackBox or NuclearMods was copied.

## Additional candidate settings and offline audit

`Capture.Scope` defaults to `World`; `Engagement` limits motion to referenced units and all locally registered missiles. `World` means locally registered units, not a guarantee that every simulated object is present. `Capture.WorldHz=10`, `Capture.ProjectileHz=20` and `Capture.MaxMotionPerBatch=512` control motion volume. Overflow rotates through the registry and reports omitted samples; failed reads and queue losses are separate. Since 0.4.1, a non-missile unit whose snapshot matches its last full snapshot is listed in `unchanged_entity_ids` (sampled at `unchanged_sample_real_time_s`) instead of in full, for up to `Capture.UnchangedRefreshSeconds=5`; readers repeat its last full pose. `Capture.RecordDuringReplay=false` turns off the bundle and demo when the game starts a replay (`-ncReplay` or `[Replay] AutoStartPath`). Limits are per batch, not a proven frame-time budget. The measured `capture_read_ms` excludes subsequent serialization and queue work. Actual performance needs a future capture.

The writer also caps queued UTF-16 payload at `Capture.QueuePayloadMiB=32` (clamped to 1–256 MiB), alongside the existing record-count bound. Object overhead and one active writer item are outside that payload limit. Oversized/full-queue records are dropped and counted in `writer-status.json`.

Motion records include transform and Rigidbody poses, quaternion orientation, velocity, angular velocity, definition keys, gear state and observed aircraft inputs. Transform and Rigidbody poses are separate because distant client entities may update them differently. Client smoothing and server/local simulation are labeled. Capture never applies recorded inputs or changes simulation state.

`projectile_observed` is not `weapon_launch`. Its launch time, original assignment and launch range stay null. `reported_start_*` fields contain replicated initialization state, which can also come from a saved missile. Request/animation events have no projectile link. Target changes and detonation can link to a first observation while `launch_event_id` stays null. Reused raw projectile IDs remain ambiguous for the rest of that mission.

A read-only coverage report can be generated outside an archived bundle:

```sh
python3 tools/replay_audit.py /path/to/bundle > /path/to/project/pose-audit.json
```

This reports pose gaps, missing orientations, sample omissions and observed storage rate. It does not render game visuals or establish working reconstruction. Keep generated analyses inside the project when inspecting the September 29 archive.
