# Changelog

## 0.6.0 (2026-10-30)

- **Bulk delete in the Replays list.** Every row has a checkbox. Shift+click ticks a range, Ctrl+click ticks
  one more, and a ticked row can be unticked again (Shift on a checkbox unticks a range). Select all and
  Clear sit under the list with the count and size ticked ("3 selected, 1.2 GB"). Delete asks once, naming how
  many recordings and how much space, then removes them with their shot lists and camera paths; clips stay.
  Recordings that cannot be deleted are named with why and stay ticked.
- **The Replays list opens at once with big libraries.** Rows show straight away from the file names, sizes
  and dates, newest first, and the rest fills in from a background reader, the rows on screen first, without
  hitching the menu. Reading a recording is one fast sequential pass. What was read is kept in
  `NuclearReplay/replay-list-cache.txt`, so later opens read only new or changed recordings. Only the rows on
  screen are built, so scrolling hundreds of recordings stays smooth.

- **Target HUD** (F4 in a replay). The game's own targeting HUD drawn over any replay camera: each unit's
  HUD icon in friend or foe colours (from the recording player's side, so they do not flip on a cut), the
  target box on what the followed unit is after, and lines from each missile to its target, solid once its own
  seeker has it. Story mode shows the followed unit's fight, Missiles every missile in flight, All every unit
  in range; a side filter shows one side's units and shots. Labels stay few and inside the safe area, salvos
  share one label, new missiles flash and destroyed units fade. Shift+F4 cycles the mode, and so does the
  "Target HUD: <mode>" button in the viewer's camera row (click: next mode, right-click: previous). Clips and camera
  paths record with it, and H does not hide it.
- Shot lists take `target_hud` per shot or at the root (`"story"`, or an object with `mode`, `focus`,
  `side`, `home`, `labels`, `arrows`, `size`, `units`). `nr shots validate` checks it. Lists without it render
  as before.

- **Lighting** (F2 in a replay): time of day presets (dawn, golden hour, dusk, night, or as recorded),
  15-minute steps, haze and weather. **Z** puts the sun behind the aircraft you follow: the free camera moves to
  the backlit side. The settings stay while you move through the replay, clips record with them, and Camera
  Studio saves them with its path. Home goes back to the recording's own light. Only the replay's look changes;
  what happened in the match stays the same.
- Shot lists take lighting fields: `time_of_day`, `sun_azimuth`, `fog`, `weather` and `cloud_height` per shot
  or as a root `lighting` default, and `backlight` (with `backlight_off`, `_side`, `_pitch` and more) to put
  `chase_cine`, `flyby`, `track_from` and `keyframe` cameras on the sun line. `nr shots validate` checks them.
  Lists without them render as before. docs/LIGHTING.md has the details.
- **Lens pack** (F3 in a replay). The Lens panel sets the focal length in mm on a Super 35 or
  full-frame sensor, depth of field (an f-stop, and focus on the target or at a set distance),
  vignette, film grain, chromatic aberration and camera motion blur. Every effect is off until you
  turn it on, and the panel keeps its settings between sessions. With the panel open, Tab picks a
  row, Page Up and Page Down change it (Shift for bigger steps) and Home resets it.
- The free camera's Ctrl + mouse wheel now changes the focal length in mm, and Alt + mouse wheel
  sets the focus distance. The bar under the timeline shows the lens in mm.
- Camera Studio keys keep the focus distance and f-stop, so a camera path can pull focus from key
  to key. The path also keeps the panel's effects.
- Shot lists take `focal_mm`, `sensor`, `dof` (`f_stop`, `focus`: `"target"` or metres),
  `vignette`, `grain`, `chromatic` and `motion_blur`, and path keys take `focal_mm`, `focus` and
  `f_stop`. `nr shots validate` checks them. Shot lists without them render as before.
- **Any unit as the subject.** Ships, ground vehicles, launchers and helicopters work with every camera except
  the cockpit. **B** opens a unit list: type to search by callsign, player name, type or side, players first,
  then by type; Enter or a click watches the unit. Its filter (aircraft, ground, sea, all) also sets what
  **[ ]** step through. Destroyed units show only when asked.
- **Two-shot** (camera 9): frames the unit and the one it is fighting together, from the side or behind, with
  a lens that widens and narrows within limits and a camera that never swings across the line. **Shift+B**
  picks the second unit by hand. Also a camera style in `nr` (`two_shot`).
- **Ride-along** (key 0): the camera flies in formation with a unit. Fly the free camera to the spot and
  press **R** to lock it there; **Shift+R** keeps the horizon level or rolls with the unit. Smooth, with
  no network jitter.
- Shot lists: the `ride` and `two_shot` cameras with their options (`offset`, `look_dir`, `look_at`, `roll`;
  `second`, `side`, `distance`, `fov_min`, `fov_max` and more), a `type:<text>` target (`type:frigate`),
  and `name:<text>` also matches player names. `nr shots validate` checks the new fields.
- **Faster timeline jumps**, for old recordings too. While a jump runs, the game steps its physics more coarsely
  (the recording still sets every unit's position, state, targets and kills), draws only a few frames and mutes
  the sound. A jump back no longer reads the file again or waits 5 s in the menu. On a 38-minute multiplayer
  recording, 1:00 to 10:00 went from 96 s to 44 s, 10:00 back to 5:00 from 61 s to 27 s, and 5:00 to the last
  minute from 6 min 8 s to 3 min 38 s. The label shows the jump's progress, and **Space** during a jump stops it
  where it is. Clips and renders are made exactly as before.
- In a replay, F1 to F4 no longer also switch the game's own debug panels (F4 opened its network stats
  panel). Outside replays the game's keys work as before.

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
