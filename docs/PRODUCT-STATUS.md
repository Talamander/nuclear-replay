# Product status (handoff)

Last updated 2026-10-12. Read this first when resuming the product work.

## Decisions (owner)

- 2026-10-09: Nuclear Context is free for everyone. The paywall is gone: no Lemon Squeezy, no license
  keys, no buy links, no `NcBetaFree` switch. The signed updater stays (removed 2026-10-12, see below).
- The GUI is for non-technical players. It stays fully open to agents: every GUI action is an `nr`
  command, "Copy command" gives the exact line for a clip, and `docs/AGENTS.md` ships next to the app.
  The app and `nr` share one engine; there is no GUI-only logic. (Superseded 2026-10-12: the GUI is gone.)
- Host-only recording is fine for v1.
- Not part of the product: narration, ElevenLabs, the Shorts pipeline, `scripts/yt-upload.py` and other
  personal tooling.
- Windows beta tester: a friend of the owner (not named in the public repository).
- 2026-10-11: license is the Zero-Clause BSD license (0BSD), "Copyright (C) 2026 Talamander". No
  attribution required. `LICENSE` at the root ships in every zip as `LICENSE.txt`. The mod will
  never be monetized; there is no website, only GitHub (https://github.com/Talamander/nuclear-replay).
- 2026-10-10: the game itself is the replay viewer. The app's live preview and in-app clip player were
  slow and soft, so they are gone. The app is the launcher, library, render queue and settings; `nr`
  and headless renders stay for agents.
- 2026-10-12: the desktop app is removed. The mod is the product: players install the plugins zip
  with NOMM or by hand and use the in-game viewer. `nr` and `NuclearReplay.Core` stay in the repository
  for the owner and for agents, built from source (`docs/DEVELOPMENT.md`); they are not released. The
  signed update manifest, the updater and the Windows/Linux app zips are gone; a release is the
  plugins zip only. The app source (with the updater, `tools/sign-manifest`, the app README shots and
  packaging texts) is archived in `~/Projects/nuclear-context-archive/app/` from commit `5d4462a`.

## Done

- Recorder writes `mission`, `player`, `launch` and `kill` events into each `.ncdemo`
  (docs/PRODUCT-CONTRACT.md). The heavy telemetry bundle can be turned off (`[Capture] TelemetryBundle`).
- Replay plugin: `-ncRenderJob` renders a job file to MP4 through a plugin-started ffmpeg (tested
  headless under Proton), and `-ncStart` starts a watch at a moment.
- Signed updates (Ed25519), see `app/RELEASING.md`.
- `scripts/package.sh` builds the Windows and Linux zips with `README.txt` and `AGENTS.md`.
- Branch `free-gui` (2026-10-09):
  - Licensing removed from Core, CLI, app, tests and docs.
  - Shared clip planning in Core (`Clips/ClipSpec`, `ClipPlanner`, `ClipCommand`) used by both
    `nr render`/`nr shots make` and the app. Parity tests prove the copied command writes the same job.
  - New camera styles `victim_cut` and `missile_follow`; kills pair with the missile launch that caused
    them; the match list shows missile hits.
  - GUI: first-run setup that lists what the installer did, match library, match page with moment list,
    timeline, camera styles, formats (16:9 1080p, 9:16, 4K), 30/60 fps and tucked-away advanced options,
    a render confirm dialog with plain-language blockers (setup, ffmpeg, game running), a Renders page
    with progress, cancel (with confirm), open folder, fix text and log paths.
  - `nr render` refuses while the game runs (`game_running`), like the app.
  - Screenshots and review: `review/gui-2026-10-09/REVIEW.md`.
- Branch `free-gui`, part b (2026-10-09), built without starting the game:
  - Live preview: the replay plugin's `-ncPreview` job streams small JPEG frames to the app over
    127.0.0.1 and takes `play`, `pause`, `shots`, `range`, `seek`, `restart`, `stream` and `quit`
    commands back (docs/PRODUCT-CONTRACT.md section 4, `shared/Preview/PreviewWire.cs`). The match page
    has a preview pane with honest loading steps, play/pause, scrub, LIVE, restart and error states.
  - Moment and match pictures from the same plugin path (`thumbs` mode), cached per recording.
    Automatic pictures are a setting, off by default.
  - In-app clip player on the Renders page: ffmpeg decodes to JPEG frames (no LibVLCSharp; see the
    review for why). No sound in the app; "Open in video player" plays with sound.
  - `nr preview` and `nr thumbs`, with `--simulate` for scripts and tests (docs/AGENTS.md). The app
    takes `--simulate-game` / `NC_SIMULATE_GAME=1`.
  - Screenshots, verified vs untested: `review/gui-2026-10-09b/REVIEW.md`.
- Branch `free-gui`, part c (2026-10-09), checked on the real game (isolated copy, headless gamescope):
  - Preview, pictures, live camera and moment changes, the in-game reload when going back in time, and
    one GUI render each of `victim_cut` and `missile_follow` work on the real game. Measured costs and
    load times are in the review.
  - Fixed: the plugin was destroyed by the first scene load (preview closed after 2 s); preview frame
    pacing (17 to 20 fps); sideways launch pictures (now `chase_cine`); missile shots swinging to the
    sky after the hit (director holds on the explosion); `victim_cut` cutting at the logged kill, seconds
    after the hit (new shot field `then`, cut just before the missile reaches the victim).
  - Preview and moments list share a resizable split on the match page.
  - Opt-in real-game GUI test: `RealGameTests` (`NC_REAL_GAME_TEST=1`, docs/AGENTS.md).
  - Screenshots with real frames, both renders, verified vs untested: `review/gui-2026-10-09c/REVIEW.md`.

- Branch `ingame-viewer` (from `free-gui`, 2026-10-10), checked on the real game (isolated copy,
  headless gamescope):
  - In-game replay viewer (`-ncViewer`, DEMO.md "In-game replay viewer"): a Replays button and list
    in the main menu; a uGUI overlay with timeline, markers, clip window, play/pause, skip, speed
    0.25x to 4x, 7 camera presets, next/previous aircraft, free camera, hide UI, game HUD toggle and
    a help panel.
  - "Clip this" records in real time at native resolution and 60 fps with game audio. It uses
    `h264_vulkan` on the GPU under Proton, because `h264_nvenc` does not start there. The measured
    fps cost is 60.0 to 58.7 (1080p, gamescope capped at 60 Hz).
  - App: **Open in game** and **Watch this in game**, and `nr open <demo> [--at] [--focus]`. Viewer
    clips are imported into Renders. Clips open in the system player. The live preview and clip
    player are removed. Moment pictures and headless renders stay.
  - `docs/JUMP-POINTS.md` is research on faster jumps; nothing is built yet.
  - Review: `review/gui-2026-10-10/REVIEW.md`.

- Release review, branch `cleanup-release` (2026-10-11), built without starting the game:
  - The Linux zip now bundles the Windows `ffmpeg.exe` by default (`NC_LINUX_BUNDLE_FFMPEG=0` leaves
    it out), so Proton renders and viewer clips work out of the box. Was open item 5.
  - `scripts/package.sh` refuses to package when a shipped text file or plugin DLL contains a home
    folder, the game drive, the owner's name or a secrets path.
  - Third-party notices cover every package that ships (BouncyCastle, ANGLE, Tmds.DBus, MicroCom and
    the .NET runtime, SkiaSharp and CommunityToolkit notices were missing).
  - User README (root), shipped READMEs and `docs/AGENTS.md` checked against the code. The old
    developer README is `docs/DEVELOPMENT.md`; test-copy notes moved to `docs/TESTING.md`.
  - Secrets sweep of the whole git history: no keys, tokens or passwords. The private update key,
    `secrets/yt-token.json` and the YouTube OAuth client were never committed.

- Branch `fix-viewer-ffmpeg` (2026-10-11), built without starting the game:
  - `nr install`, Game setup and `nr update apply` write the effective ffmpeg (the setting or the
    bundled `ffmpeg.exe`) and the app's clips folder into
    `BepInEx/config/talamander.nuclearcontext.replay.cfg` (`[Viewer] FfmpegPath`, `ClipsDirectory`)
    as game-side paths (`Z:\` under Proton). Only missing or empty keys are filled; no ffmpeg found
    skips the step. BepInEx reads the values before the plugin binds them, so "Clip this" works in a
    game started from Steam without the app opening a replay first (was checklist item 5). The
    README notes about opening a replay from the app first are gone.

- Branch `ingame-viewer`, release decisions (2026-10-11), built without starting the game:
  - Plugin GUIDs are `talamander.nuclearcontext` (recorder) and `talamander.nuclearcontext.replay`
    (was `local.nuclearoption.context*`); Harmony ids follow. Each plugin copies its old
    `BepInEx/config/<old GUID>.cfg` to the new name on its first start (before any `Config.Bind`,
    then `Config.Reload()`); the old file stays. The installer's `[Viewer]` writer copies first too, and
    the app reads either file (`shared/Plugin/PluginIds.cs`, compiled into both plugins and Core).
    `tests/ConfigMigrationTests.csproj` runs the copy and reload against the real BepInEx `ConfigFile`.
    Not yet checked in the game: settings survive an update from 0.4.x.

- Branch `ingame-viewer`, ffmpeg on first use (2026-10-12), built without starting the game:
  - The zips no longer bundle ffmpeg (`NC_LINUX_BUNDLE_FFMPEG` is gone; this replaces the Linux
    bundle from `cleanup-release`). The pin (URL, size, SHA-256) is in
    `shared/Ffmpeg/FfmpegPackage.cs`, compiled into Core and the replay plugin: gyan.dev FFmpeg 9.0.2
    full build from the GyanD/codexffmpeg GitHub release (258 MB zip, 228 MB `ffmpeg.exe`).
  - Everything shares `<game>/NuclearContext/tools/ffmpeg/ffmpeg.exe`. Order: the `ffmpeg_path`
    setting, the shared copy, then a development `bundled/ffmpeg`.
  - Game setup has a "Video encoder" card (download, progress, cancel, error, try again) and
    downloads after Install; `nr install` downloads too (`--no-ffmpeg` skips it, a failed download
    does not fail the install); `nr ffmpeg status|install`. Render blockers point at the download.
  - The replay plugin asks before downloading when C finds no ffmpeg, downloads with
    UnityWebRequest, checks the SHA-256 and unpacks on a background thread, and shows progress and
    errors in an overlay panel. `-ncFfmpeg` and `[Viewer] FfmpegPath` still win.
  - The installer's `[Viewer] FfmpegPath` is the shared location when there is no setting, so the
    viewer finds the copy once downloaded.
  - Not yet checked in the game: the prompt, the download under Unity's Mono (UnityWebRequest and the
    Deflate unpack), and a clip with the downloaded ffmpeg.

- Branch `ingame-viewer`, NOMM listing prep (2026-10-12), built without starting the game:
  - `scripts/package.sh` also writes `NuclearContext-<version>-plugins.zip` (both plugin DLLs,
    `LICENSE.txt`, `README.txt` at the zip root). It is the first release asset (`app/RELEASING.md`).
  - Draft NOMNOM entry in `packaging/nomnom/` (id `NuclearReplay`, placeholders for the release URL and
    hash). Checked against NOMM's installer: same folder name means each tool sees the other's install;
    the app reports a mod turned off in NOMM (`BepInEx/disabledPlugins`).
  - NOMNOM parses release tags as `System.Version`, so `-beta.N` tags are never picked up. The plugins
    already report `0.5.0` (`BuildVersion.Core`); the first public tag should be `v0.5.0` with
    `NcVersion` 0.5.0.

- Branch `ingame-viewer`, rename to Nuclear Replay (2026-10-12), built without starting the game.
  This supersedes the names in the entries above:
  - One plugin assembly, `NuclearReplay.dll` (`NuclearReplay.csproj`, replay code in the namespace
    `NuclearReplay.Playback`), with the plugins `talamander.nuclearreplay.recorder` and
    `talamander.nuclearreplay.player` (soft dependency on the recorder). Folder
    `BepInEx/plugins/NuclearReplay/`; data folder `<game>/NuclearReplay/`.
  - Config: each plugin copies the newest old file (`talamander.nuclearcontext*.cfg`, then
    `local.nuclearoption.context*.cfg`) once. A recorder `OutputDirectory` still at the old default
    moves to the new default; the old folder stays.
  - Old `<game>/NuclearContext/demos` is listed by the app, `nr list` and the in-game Replays menu; the
    ffmpeg lookup also checks `<game>/NuclearContext/tools/ffmpeg`. Recording writes only to the new
    folder.
  - The installer moves `BepInEx/plugins/NuclearContext/` to its backup folder. A loaded legacy GUID
    turns the recorder off and shows a warning in the main menu (hint line and a toast).
  - CLI `nr` (was `nc`), app `NuclearReplay`, zips `NuclearReplay-0.5.0-plugins.zip`, `-win-x64.zip`,
    `-linux-x64.zip`. `NcVersion` 0.5.0. The update manifest product stays `nuclear-context`.
  - ffmpeg stays on the gyan.dev full build: the essentials build of 9.0.2 has no `h264_vulkan`
    (built without `--enable-vulkan`), the GPU encoder clips use under Proton.
  - Not yet checked in the game: the merged DLL, the config copy, the legacy warning.

- Branch `ingame-viewer`, desktop app removed (2026-10-12):
  - Removed: `app/src/NuclearReplay.App`, `app/tests/NuclearReplay.App.Tests`, `tools/readme-shots/app`,
    `tools/sign-manifest`, the app screenshots, the app zip texts and `packaging/licenses`.
  - Removed from Core and `nr`: `Updates/` (manifest, signature check, updater, `SemVer`),
    `Services/` (`IUpdateService`), `ProductConfig`, the `check_for_updates` setting, `nr update`,
    `RenderQueue` and `ViewerClips` (the app's Renders list and its import of viewer clips),
    `AgentGuide` (the app's "Copy agent guide") and the BouncyCastle package.
  - `scripts/package.sh` writes only `NuclearReplay-<version>-plugins.zip` and `SHA256SUMS`, with the
    same leak check.
  - The plugin still writes viewer-clip records (`<game>/NuclearReplay/viewer-clips/`) and accepts
    `-ncViewer`, `-ncFfmpeg`, `-ncClipsDir` and `-ncPreview`; `nr` and scripts use them.

## Release checklist

In order. "Owner" items need Tal; nobody else can do them.

### Blocks the first public release

1. **Owner: the Shockfront Studios answer** on distributing a mod for Nuclear Option. Nothing ships
   before it.
2. Done 2026-10-11: **license.** 0BSD (`LICENSE`, shipped as `LICENSE.txt`). Everything bundled is
   MIT, BSD, OFL, LGPL or GPL as a separate program, so a permissive license fits.
3. **Windows beta test.** Nothing has run on real Windows yet: install by hand, recording, the
   viewer and "Clip this" with the ffmpeg download, NVENC.
4. **The in-game viewer in a normal visible window:** real mouse and keys, the right-mouse cursor lock
   under Proton, sound, and the Steam launch path (`nc-viewer-test` runs the test copy visibly). The
   headless checks used the command hook.
5. Done in code (branch `fix-viewer-ffmpeg`, 2026-10-11), not yet checked in the game: **viewer
   clips on a fresh install.** The installer now fills the replay plugin's `[Viewer] FfmpegPath` and
   `ClipsDirectory`. Since 2026-10-12 the plain install is the plugins zip by hand or NOMM: with both
   keys empty, C asks to download ffmpeg and the clip lands in `<game>/NuclearReplay/clips`.
6. Dropped 2026-10-12: **the update host.** There is no updater any more. NOMM finds new releases
   by itself; players who installed by hand download the new zip. Releases must still not be marked
   pre-release.
7. Dropped 2026-10-12: **the signing key.** Nothing signs or checks a manifest now.
   `secrets/update-signing.key` is unused; keep it or delete it, it does not matter for releases.
8. Done 2026-10-12: **FFmpeg source.** The zip does not contain ffmpeg; the game (and `nr`) download
   it from the gyan.dev release on request, so no source mirror is needed next to releases.
   To check in the game: a fresh install without ffmpeg, press C, answer Yes, then record a clip.

### Before the repository goes public

9. Done 2026-10-11 in the tree: `review/` (about 140 MB of game screenshots and clips) is no longer
   tracked and is in `.gitignore`. The files stay on disk in each checkout that has them, so the
   `review/...` links in this file work locally only. They are still in the git history; the owner plans
   to publish the public repository with a fresh history.
   Also done 2026-10-11: the visual-capture research (`playback/`, its tests and tools, the visual
   record formats in `shared/`, and the root `HOOKS.md`, `RECONSTRUCTION.md`, `VALIDATION.md`,
   `CONTINUE-CINEMATICS.md`) moved to a research archive outside the repository; `docs/HOOKS.md` keeps
   the recorder's current hook list. `research/` (decompiled game code, upstream clones, test
   evidence) is gitignored and was never tracked.
10. Personal tooling and paths: `scripts/yt-upload.py` (YouTube upload, owner only), the render and
    test-copy scripts, and the default `GameDir` in the `.csproj` files point at the owner's machine
    (`/mnt/games/...`). None of it ships; decide what a public repo should keep.
11. Done 2026-10-11: the JSON schema `$id`s are raw GitHub URLs on `main`
    (`https://raw.githubusercontent.com/Talamander/nuclear-replay/main/app/schemas/...`). They resolve
    once the repository is public with a `main` branch.

### Should have

12. Dropped 2026-10-12: code signing for the Windows exe (there is no exe in the release).
13. In-game checks still open: `-ncStart` watch, gun kills, multiplayer event markers, missile shots
    where the missile misses, and weak picture framing on some kills (review part c).
14. A download page and a trailer from replays.

### Release steps

15. Bump `NcVersion` in `version.props`, then build: `GAME_DIR=... scripts/package.sh`.
16. Check the zip: `unzip -l dist/NuclearReplay-<version>-plugins.zip` lists only `LICENSE.txt`,
    `NuclearReplay.dll` and `README.txt`. Before tagging, grep the sources once:
    `git grep -inE '/home/|/mnt/|talamander' -- app/src src replay/src shared` (doc comments naming the
    owner's player name, the `talamander.nuclearcontext*` GUIDs and the `Talamander/nuclear-replay`
    GitHub URLs are fine).
17. Tag `v<version>` (a plain numeric tag such as `v0.5.0`, so NOMNOM can parse it), create the GitHub
    release as a draft with the plugins zip first, then `SHA256SUMS`, then publish it, not as a
    pre-release (`app/RELEASING.md`).

### After the first public release

- **Owner: list the mod in NOMM** (after the rename pass): fill in the placeholders in
  `packaging/nomnom/NuclearReplay.json` and open a pull request to NOMNOM (`packaging/nomnom/README.md`).
