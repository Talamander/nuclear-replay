# Changelog

## 0.5.2 (2026-10-27)

- Record matches you join as a client. The recorder writes `<time>-<mission>-client.ncdemo` with what the
  server sent you; your own aircraft replays like every other unit, launches included. On by default;
  `[Demo] RecordAsClient = false` turns it off. Replays never record themselves.
- **Delete recordings** from the Replays screen. The first click on Delete (or the Delete key) asks,
  a second one deletes the recording and its sidecar files. The question says how big the recording
  is, and the screen's header shows how much space all recordings take.
- The Replays screen now closes when you open another main menu screen or press Escape.
- **Action cam** (camera 8): a chase camera on a springy arm that reacts to how the aircraft flies. It
  swings out in turns, drops back under afterburner, leads into the turn and shakes a little with G.
- **Camera Studio** (P in a replay): fly the free camera and press K to lay down keys, then play the
  camera path or clip it. Paths can stay fixed in the world or move with an aircraft, aim along the
  keys, at the aircraft or along the path, and run at even speed or hit every key at its time. Each
  recording keeps its path in `<recording>.path.json`, which also works as a shot list.
- **Cinematic chase** (camera 1) is now its own camera: a smooth chase from low and to the side that
  trails the aircraft's turns, instead of the game's chase camera with a wider lens. Chase (camera 4)
  stays the game's camera. `nr`'s `cinematic_chase` style uses it too.
- **Cockpit** (camera 7) shows the tactical display on the instrument panel and the rest of the crew,
  such as the back-seater. The game builds the display only for your own aircraft and hides the whole
  crew in its cockpit camera.
- **Ctrl + mouse wheel** zooms the free camera's lens.
- **Camera shake** for every cinematic camera in shot lists: `handheld`, `rumble` and `buffet` layers,
  the same on every render of a clip.

## 0.5.1 (2026-10-12)

- Kill marks land on the hit. The game's kill feed comes when the victim dies, often 2 to 3 seconds after
  the missile hits it. The recorder now also notes when the killer's weapon last hit the victim (`hit_ago`
  on the `kill` event: seconds from that hit to the kill feed). The viewer's timeline marks, kill labels,
  jump to a kill, clip windows and the victim cut camera use that moment, and so does `nr` (`t` of a kill
  is the hit; `kill_feed_t` in `nr events --json` keeps the kill feed time).
- Recordings made before 0.5.1 have no hit time and work as before, at the kill feed time.

## 0.5.0 (2026-10-12)

First public release. The mod is now called Nuclear Replay (it was Nuclear Context).

Changed before release (2026-10-12): the desktop app is gone. The release is one zip,
`NuclearReplay-<version>-plugins.zip`, for NOMM and for installing by hand. The notes after this list are kept as
they were written; where they disagree, this list wins:

- Removed: the desktop app, the Windows and Linux app zips (and the BepInEx copy they bundled), and
  the signed updates. NOMM finds new releases by itself.
- `nr` stays as a developer tool, built from source (`docs/DEVELOPMENT.md`). It is not in the release.
  `nr update` is gone.
- Clips made in the game are saved to `<game>/NuclearReplay/clips` unless `ClipsDirectory` is set.
- Clip names are `<mission> m-ss to m-ss.mp4` in whole seconds, with ` (2)`, ` (3)` and up when the
  name is taken, in the game and in `nr`.
- Viewer fixes: the "Clip saved" message fits its text and names only the file (the encoder and
  frame rates go to the log); the status line shows the selected moment only near it; the F1 help panel
  fits its text; the Replays list hides the Map column when no recording names a map; the
  highlighted row follows the selection.

Notes as first written:

- The recorder saves every match you host as a network demo (`.ncdemo`) with mission, player,
  launch and kill events.
- In-game replay viewer: a Replays button in the main menu, a timeline with markers, play, pause,
  skip, speed 0.25x to 4x, 7 camera styles, next and previous aircraft, a free camera, and
  "Clip this" to record a clip at screen resolution and 60 fps with game audio.
- Desktop app for Windows and Linux: installs BepInEx and the mod (and the Proton override on
  Linux), lists your matches and moments, opens a match or a moment in the game, renders clips in
  the background (16:9 1080p, 9:16 vertical or 4K, 30 or 60 fps), and lists all clips on one page.
- Camera styles for clips: cinematic chase, victim cut, missile follow, chase, orbit, flyby and
  cockpit.
- `nr` command line tool with JSON output. Every app action is an `nr` command, and "Copy command"
  gives the exact line for a clip.
- Signed updates from the project's GitHub releases.
- A plugins-only zip (`NuclearReplay-<version>-plugins.zip`) for mod managers such as NOMM and for
  installing by hand into `BepInEx/plugins/NuclearReplay/`.
- Both app zips bundle BepInEx 5.4.23.5. FFmpeg is not included: Game setup, `nr install`,
  `nr ffmpeg install` and the in-game "Clip this" prompt download a pinned Windows build (FFmpeg
  9.0.2, about 258 MB from github.com, checked by SHA-256) into `<game>/NuclearReplay/tools/ffmpeg`.
- New name: Nuclear Replay. The whole mod is one DLL, `NuclearReplay.dll`, in
  `BepInEx/plugins/NuclearReplay/`. It holds two plugins with the GUIDs
  `talamander.nuclearreplay.recorder` and `talamander.nuclearreplay.player`. The app is
  `NuclearReplay`, the command line tool is `nr` (was `nc`).
- Coming from Nuclear Context: your settings move over by themselves. On the first start each
  plugin copies its newest old config file (`talamander.nuclearcontext*.cfg`, then
  `local.nuclearoption.context*.cfg`) to the new name; the old files stay. The app copies its
  settings and render list once from the old "Nuclear Context" folders.
- New recordings go to `<game>/NuclearReplay/demos`. The old `<game>/NuclearContext` folder is never
  moved or deleted; its recordings still show in the app, `nr list` and the in-game Replays menu, and
  an ffmpeg already downloaded there is used.
- Installing with the app or `nr install` moves an old `BepInEx/plugins/NuclearContext` folder into
  the install backup. When the old plugins are still loaded, the mod says so in the main menu and
  does not record until you delete that folder.
- Historical names stay: `.ncdemo` files, the `-nc*` game arguments and the `NC_*` environment
  variables.
