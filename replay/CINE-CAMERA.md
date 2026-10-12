# Cinematic camera and engine mattes (replay director)

Built 2026-10-03 for the long-form documentary. It adds four director-owned camera modes and an optional per-frame aircraft matte to the replay plugin. Every existing camera mode and shot list renders as before.

## What was built

- **Director-owned camera** (`replay/src/CineCamera.cs`). It is a `CameraBaseState` subclass that the director switches the game's `CameraStateManager` into, so audio, LOD, the floating origin and rendering stay the game's own, but every pose comes from the director. All positions are kept in global coordinates (floating-origin safe). Position, aim, lens and lead room are smoothed with critically damped springs (Unity `SmoothDamp`). The aim has velocity feed-forward, so a straight pass has no steady-state lag. Moving rigs smooth the offset from the target, not the world position, so a 400 m/s jet does not drag the camera behind its keyed position. The camera is clamped above terrain and the sea (soft floor spring plus a hard minimum). The horizon is level unless `horizon_lock` is false. Shadow distance follows the subject distance and is restored after the shot.
- **Shot types** `flyby`, `chase_cine`, `track_from`, `keyframe`. Options are listed in `DEMO.md` ("Cinematic cameras"). Highlights:
  - `flyby` takes its pass point from the future trajectory (`track` CSV with `track_t0`), from an explicit `pass` point, or from the velocity. It then checks line of sight from the chosen spot to the path (`visible_span` seconds either side of the pass) and, if terrain hides the path, searches the other side, wider offsets and higher spots for the most visible one.
  - `track_from` can ride on a ground unit (`unit:<id>`, `name:`, `nearest:`), with `from_offset` and `from_toward` to clear the unit's own superstructure and smoke.
  - `keyframe` interpolates positions with a Catmull-Rom spline, eases the timing (smootherstep blended by `ease_amount`), and interpolates look points and lens (in log space).
  - Lens: fixed `fov`, keyed `fov`, or auto zoom `size` (target at that fraction of frame height) between `fov_min` and `fov_max`.
- **Lens workaround for NOVR.** The installed NOVR mod patches `Camera.fieldOfView`'s setter to do nothing, and it renders through its own child camera ("NOVR Main Camera"). The cine camera detects this, applies the lens through the projection matrix of the camera that actually renders, and raises `QualitySettings.lodBias` by the zoom factor so LOD selection matches the long lens. Everything is reset when the cine shot ends. Without NOVR the normal `fieldOfView` path is used. The game's own camera modes keep their old behaviour, which means their `fov` field still has no effect while NOVR is installed (it did not work before either).
- **Engine mattes** (`replay/src/EngineMatte.cs`). A shot with `"mattes": ["aircraft"]` makes a second, off-screen camera render only the target aircraft (its mesh renderers move to a spare layer only while that camera renders) with the same pose and projection as the rendering camera, cleared to transparent black. Its alpha channel is read back asynchronously and written as `range_NN/matte.gray`; `scripts/render-clips.sh` encodes it as `<clip>_matte.mp4`. Verified pixel-aligned with the colour frames (see `05_matte_example`). The readback is bottom-up, so the encoder flips it (`MATTE_VFLIP=0` disables that). Ranges with no matte shot drop their matte file.
- **Not built:** a depth matte. The aircraft matte pass would be the place to add it (render depth to a second target), but it was not needed for the demo and was not attempted.

## Files changed

Backups have the suffix `.orig-2026-10-03`.

- `replay/src/CineCamera.cs` (new)
- `replay/src/EngineMatte.cs` (new)
- `replay/src/Director.cs` (backup `Director.cs.orig-2026-10-03`): `Shot.Raw` (the shot's JSON, read by the cine camera) and `Shot.Mattes`; cine shots switch into the cine state; non-cine shots end it; matte properties for the plugin.
- `replay/src/ReplayPlugin.cs` (backup `ReplayPlugin.cs.orig-2026-10-03`): creates the matte renderer when a shot list asks for mattes, writes `matte.gray` per range, adds `"matte":"gray"` to the range `.done` marker.
- `scripts/render-clips.sh` (backup `render-clips.sh.orig-2026-10-03`): encodes, cuts, caches and delivers `<clip>_matte.mp4`.
- `DEMO.md` (backup `DEMO.md.orig-2026-10-03`): new sections "Cinematic cameras" and "Engine mattes".
- Jitter fix (2026-10-03, later the same day): `CineCamera.cs` and `EngineMatte.cs` have backups `.orig-2026-10-03` taken before it; `ReplayPlugin.cs` gained the trace frame index and the preroll capture step (its backup is the pre-cinematics original).
- `dist/NuclearContext.Replay.dll` and the installed plugin DLL (backups `NuclearContext.Replay.dll.orig-2026-10-03` in both places).

## Usage

Write the shots into a shot list and render as usual, for example:

```
scripts/render-clips.sh "<demo>.ncdemo" "1479:1487,1834.5:1842,1842.5:1850.5,3046.5:3054.5" out/clips shots.json
```

The demo shot list is `/mnt/games/nuclear-option-content/2026-10-01-confrontation/longform/fx/camera/test6/shots.json`. A cine shot logs its setup and a pose line every 0.5 s to the BepInEx log (`Cine <mode> t+...: cam ... target ... dist ... fov ...`), which is the quickest way to tune offsets.

QC helper: `fx/camera/tools/qc.py clip.mp4 <first replay second>` (run with the `depth-title/.venv` Python) writes a 4x4 contact sheet with a thirds grid and prints motion stats. A "whip" there is a sustained change of pan rate of more than 60 px/frame. One-frame spikes are listed separately; they are phase correlation locking onto the subject and were checked to be smooth.

## Demo reel

`/mnt/games/nuclear-option-content/2026-10-01-confrontation/longform/fx/camera/demo/` holds the 1920x1080 30 fps H.264 clips with game audio from the OBS recording (track a:1, offset 72.053 s), plus a contact sheet for each:

- `01_keyframe_1479-1487.mp4`: a move from ahead of the jet, around its side, to behind it.
- `02_flyby_1834.5-1842.mp4`: the low run toward the corvette, a 6-35 degree auto zoom from a spot 8 m above a hillside.
- `03_track_from_corvette_1842.5-1850.5.mp4`: a long lens from the corvette that fires the gun, with flak and tracers, ending at the kill.
- `04_chase_cine_3046.5-3054.5.mp4` and `04_chase_cine_3046.5-3054.5_matte.mp4`: a low-side chase with slow orbit drift over the mountains, plus its matte.
- `05_matte_example_3046.5-3054.5.mp4`: the matte applied (colour jet over a desaturated, darkened background).

## Jitter fix

Review note: "some of the chase cameras are a little bit jittery".

### Measurement

`fx/camera/tools/qc.py` now reports jitter. The jet's screen position per frame is the engine matte centroid (px at 1920 wide). `acc_rms`/`acc_p95` are the RMS and 95th percentile of its frame-to-frame second difference (px/frame²), and `hf_rms` is the RMS distance from a Gaussian-smoothed copy (sigma 3 frames), i.e. shake above about 2 Hz. With `--trace` the same numbers are given for the target's world path, the camera path, the target-minus-camera offset and the camera angles, from the per-frame trace the plugin now writes for every cine shot.

Before and after, same shots with mattes on all four (`fx/camera/test7` before, `fx/camera/test8` after, one render each):

| Clip | acc_rms px/f² | acc_p95 px/f² | hf_rms px |
|---|---|---|---|
| 01 keyframe | 14.29 → 1.72 | 30.0 → 3.7 | 16.8 → 3.1 |
| 02 flyby | 12.58 → 3.12 | 10.8 → 3.8 | 9.2 → 8.4 |
| 03 track_from | 1.23 → 0.52 | 2.6 → 1.1 | 1.1 → 0.3 |
| 04 chase_cine | 23.93 → 2.33 | 61.2 → 5.0 | 19.1 → 3.2 |

The 02 `hf_rms` stays high because a close flyby really does whip the target across the frame; its frame-to-frame acceleration dropped by 4x. The target's world path went from 0.9-1.6 m/frame² of acceleration (nonphysical, about 80-150 g) to 0.10-0.16.

### Cause

The trace showed the camera already ran after the aircraft were placed (CameraStateManager's LateUpdate has execution order 2, after SendTransformBatcher's LateUpdate, which places remote aircraft), and the network interpolation clock advanced by a steady 0.0334-0.0337 s per exported frame. The noise was in the target itself. A replayed aircraft is a remote unit: every frame `SendTransformBatcher` lerps between its 20 Hz snapshots 125 ms in the past and extrapolates forward with the snapshot velocity and an acceleration estimate. The resulting position wobbles about 1 m (up to 5-8 m) along the flight path from frame to frame, while the snapshot velocity is smooth (the path from integrating it has 0.01-0.02 m/frame² of acceleration). Remote aircraft also have no Rigidbody interpolation. The camera position followed the raw target rigidly, but the look point was smoothed, so each wobble turned the camera by a few milliradians: 30-70 px hops at a 50 degree lens on the chase. Feed-forward from the target velocity was not the problem.

A smaller issue: the first exported frame of each range was a 1/60 s step (the fast-forward step), not 1/30.

### Fix

- `CineCamera.ReadTarget` keeps a filtered target position: it integrates the snapshot velocity (trapezoid) and pulls toward the raw position with time constant `target_smooth` (default 0.3 s; 0 = raw). This is a complementary filter, so there is no lag on straight or steadily turning flight. A jump of more than 30 m (respawn, new target) snaps.
- `smooth_render` (default true) draws the target at the filtered position just before the camera pose is computed (`unit.transform.position`), so the jet and the camera agree. The network transform places it again the next frame, so nothing else is affected. Residual offset from the game's own placement is about 1 m.
- The heading, chase offset, look feed-forward and lead room all use the snapshot velocity, which is already smooth.
- `ReplayPlugin`: in the preroll before a range the capture step is the export rate, so every exported frame is exactly 1/fps.
- Diagnostics: per-frame trace CSV for every cine shot (see DEMO.md), `qc.py --matte/--trace/--json`, and `fx/camera/tools/before_after.py`.

### Deliverables

- `fx/camera/demo/01`-`05` were re-cut from the fixed render (with game audio, as before). The pre-fix versions are in `/mnt/games/nuclear-option-content/tmp/trash/20261003T155108/demo-pre-jitter-fix/`.
- `fx/camera/demo/jitter_before_after.mp4` (9 s, 1280x1440): a 2x crop that follows the jet (matte centroid smoothed over 0.4 s so the crop does not hide the shake). Before is on top and after is below, first 04 chase_cine and then 01 keyframe. `jitter_before_after_strip.jpg` shows 6 consecutive frames of each.

## Known limits

- Tested only on headless renders of the 2026-10-01 Confrontation demo.
- Only the cine target is smoothed. Other aircraft in frame (wingmen in a flyby, for example) still show the network wobble. Contrails and missiles leave from the game's raw positions, about 1 m off the drawn jet.
- The jet's attitude still comes from the slerped snapshots. No attitude jitter was visible in the crops.
- When the target dies, the cine camera holds its last pose, and then the director's normal `hold` logic cuts to the nearest live aircraft with the same shot settings.
- `flyby` visibility is checked against static geometry only (terrain and buildings), not against other aircraft, smoke or clouds. The track CSV must use global coordinates.
- The `track_from` lead-room side can drift toward the centre when the target flies straight at the camera.
- With a very long lens, distant haze is strong. This is the game's fog and it reads as realistic, but it lowers contrast.
- The engine matte ignores occlusion and excludes particles. It needs the raw frame path (`render-clips.sh` fast mode), not PNG output.
- Under NOVR the lens goes through the projection matrix. Effects that read `Camera.fieldOfView` directly (for example the game's bullet tracer width) still see the original field of view.
- The regression render of `3092-3104` with the original long-form shot list has the same framing and camera moves as the reference clip. Debris and explosion particles differ, because the game's physics effects are not deterministic between runs.

## Mount close-ups (`mount`)

Tight shots of the airframe itself: the pilot through the canopy, along the wing, the gear, the nozzles, a weapon leaving its rail. The camera is rigidly attached to the aircraft's local frame (x right, y up, z forward), like a GoPro or a camera-car rig, so it rides through every roll and bank. Code: `replay/src/CineMount.cs` (new, a partial of `CineCamera`); `CineCamera.cs` only gained the `mount` hooks in `Begin`, `Step`, `End` and `OnLeave`.

```json
{"t":1219.1, "camera":"mount", "target":"host", "preset":"canopy_front", "drift":[0.15,0.05,-0.2], "arc":6,
 "drift_time":6.9, "mattes":["aircraft"]},
{"t":2940.6, "camera":"mount", "target":"host", "preset":"weapon_release", "weapon":"Scimitar", "side":"left"}
```

### Pose and smoothing

- Position: the jitter fix's filtered target position (`target_smooth`, default 0.3 s).
- Rotation: a second-order (alpha-beta) quaternion tracker on the snapshot rotation, time constant `rot_smooth` (default 0.12 s). It tracks angular velocity, so steady rolls have no lag; errors over 30 degrees snap. At close range raw snapshot rotation noise is very visible, so this filter is required.
- The target is drawn at the smoothed position AND rotation every frame before the camera pose is computed, so the airframe and the camera are one rigid body. The network transform places the unit again the next frame.
- `horizon_lock` is off by default for mounts. A number from 0 to 1 slerps the camera's up vector part way toward world up (partial lock); `true` is a full lock.
- `drift` `[x,y,z]` metres (local) and `arc` degrees (about local up, through the look point) ease in over `drift_time` seconds (smoothstep, default 6), so mounts are not static.

### Presets and anchors

At shot start `Discover` reads the airframe's anchors in local space:

- eye: `Unit.cockpitViewPoint`; canopy: the `Canopy` component.
- gear: `LandingGear` transforms; the nose gear is the one with the largest z near the centreline (|x| < 0.3), the rest are main gear.
- nozzles: `JetNozzle` components (aft-most kept), else parts named `nozzle*`/`hub*`.
- hardpoints: `weaponManager.hardpointSets[].hardpoints[].transform`.
- wingtips: `UnitPart` named `wingtip_L/R`, else a collider probe outward along x.
- bounds: from part pivots and renderer centres within 60 m (renderer `localBounds` are unusable, some are tens of km).

Presets (`side`: `right` default or `left`; any preset accepts `offset`/`look`/`look_dir`/`fov` overrides and `nudge`/`look_nudge` additions):

- `canopy_front`: ahead of and above the canopy, looking back at the pilot's helmet, fov 45. The camera is probed out from the eye onto the hull colliders, then given clearance.
- `canopy_over_shoulder`: behind and above one shoulder, looking forward past the pilot, fov 62.
- `wing_root`: under the wing root, looking out along the wing so the pylon stores are in frame, fov 58.
- `wingtip_back`: above and ahead of the wingtip, looking back at the fuselage, fov 62.
- `belly_gear`: under and ahead of the main gear on one side, looking back at the leg and bay, fov 58. Best on takeoff, landing or a gear transition.
- `nozzle`: behind and above the nozzles, looking forward and down into the exhaust, fov 58.
- `weapon_release`: below and behind the station that will fire, fov 55. The station is the one whose weapon name or short name contains `weapon` (e.g. `"weapon":"Scimitar"`), else the current station; `weapon_index` overrides the rail. The next rail comes from the station's private `weaponIndex` at shot start.

Per-airframe dumps of all anchors, stations, parts, LOD groups and colliders are written to `<game>/NuclearReplay/traces/airframe_<type>.json` the first time a type is mounted (present for KR-67, A-19, FS-12, FS-20, VT-7, CI-22, UH-90, T/A-30). The presets are generic and anchor-driven, and worked on all five tested airframes without per-airframe tuning.

### Near clip, LOD, pilot, hull

- `near` (default 0.05 m) is set on the main and render cameras for the shot and restored after it.
- `lod0` (default true): `LODGroup.ForceLOD(0)` on every LOD group of the target (9-19 per jet), released with `ForceLOD(-1)` after the shot. No LOD pops in the demo clips.
- `pilot` (default true): `Pilot.TogglePilotVisibility(true)`. The pilot model renders in third person anyway; only the cockpit camera hides it.
- `interior` (default false, untested): enables the cockpit interior renderers too (private `cockpitRenderers` via reflection). The exterior canopy already shows the pilot.
- `hull_check` (default true): if the camera is inside a convex collider (or inside a concave one by ray parity), it is pushed out along the look-to-camera direction in 0.1 m steps until it has `clearance` (default 0.25 m). Weapon and pilot colliders and triggers are ignored. The push is logged.
- Engine mattes work unchanged (`"mattes":["aircraft"]`).

### Demo

`fx/camera/demo/closeups/` (1920x1080, 30 fps, OBS game audio): 8 clips with `_matte.mp4` and `_sheet.jpg`. Packaged with `fx/camera/tools/closeups.py <clips_dir> <out_dir> name:start:end[:clip_start:clip_end]`, which muxes OBS track a:1 at replay time and trims if needed. Shot lists: `fx/camera/test9`, `test10`, `test11`.

1. `01_canopy_front_ifrit` 1219.5-1226: pilot through the canopy, Scythe launch.
2. `02_canopy_over_shoulder_brawler` 2644.5-2651: banked rocket/AGM ripple over the pilot's shoulder.
3. `03_wing_root_vagrant` 274.5-278.8: under the wing, pods and AGM-48s leaving the rails.
4. `04_wingtip_back_revoker_wingman` 2372.5-2378.5: wingman's FS-12 Revoker hit, fire on wing and fuselage.
5. `05_belly_gear_brawler_landing` 2784-2791: main gear and bay door down to touchdown.
6. `06_nozzle_ifrit_afterburner` 1105.5-1111.5: twin nozzles, afterburner lights on the takeoff roll.
7. `07_weapon_release_vortex` 2941-2946: four Scimitars leave the wing rails.
8. `08_weapon_release_revoker` 4554.5-4560: PAB-250LR bombs drop from the open bay.

qc.py: matte centroid acc_rms 0.06-1.46 px/f² (07 is 9.3 only because the matte shape changes as the missiles leave; p95 0.60).

### Known limits (mount)

- The demo has a network-time rewind at replay 278.9 s (interpolation time jumps back 2.28 s, the target 784 m). Any shot across it pops; clip 03 ends at 278.8.
- The filtered position can differ from the game's placement by 2-4 m during hard manoeuvres. The camera is rigid with the drawn jet, so this is invisible, except that the drawn jet can sit slightly off its own contrail/missile origins or, near the ground, off the runway.
- `weapon_release` picks the rail at shot start; start the shot shortly (about 0.4 s) before the launch.
- `interior` is untested.

## First-person POV (2026-10-03b)

Owner note: "all of the first person angles we've used in our videos are kind of weird. They're tilted off to the left, the bottom-left corner of the cockpit. They don't really show very much."

### What was wrong

Every first-person shot in the long-form v3 (`co_dive`, `w_cock2`, `c_cock1`, `c_cock2`, `b_cock`, `l_dive`) and in the Shorts v2 (`"obs": true` entries) is the OBS recording of Tal's VR session, used full frame (v3) or as a 608x1080 slice (Shorts). Nothing in the replay was used. Sheets: `fx/camera/demo/pov/before/obs_v3_cockpit_sheet.jpg` and `obs_shorts_crops_sheet.jpg`.

- The OBS picture is NOVR's desktop mirror: one eye of a head-tracked VR camera, cropped to 16:9. It points wherever Tal's head points, which during play is mostly down and left at the MFD and the side canopy. The forward view (HUD centre, nose line) sits near or past the right edge of the frame, so the canopy bow cuts diagonally across the left half.
- The tilt is partly real head roll and partly perspective. Measured from the panel edges (LSD line angles on the cockpit pixels): `c_cock2` rolls -15 to +25 deg and `l_dive` -9 to +19 deg, but `w_cock2` and `b_cock` stay within 0-5 deg. In those two the "tilt" is the off-axis view of the canopy bow, not roll.
- The NOVR HUD is head-locked (it follows the head with a smoothed reference), so it does not mark the aircraft's forward direction.
- The Shorts' 9:16 slices (608 px of 1920) mostly land on the MFD and panel, so they show even less of the outside world.
- The game's own `cockpit` camera in the replay (`rejected/00_game_cockpit_camera_1700-1704`) shows an unanimated cockpit with blank MFDs and no HUD. It is not usable as is.

### Replay POV (`mount` presets `pov`, `pov_shoulder`)

Code: `replay/src/CineMount.cs` (backup `CineMount.cs.orig-2026-10-03b`). `CineCamera.cs` is unchanged (backup taken anyway).

- `pov`: the camera sits at `Unit.cockpitViewPoint` (the game's own eye point). It is rigid with the smoothed airframe pose from the mount code, so it rolls with the jet and the cockpit stays level in frame. It looks along the nose, pitched by `pitch` (default 0; the first render used -6, which put too much panel in frame) and turned part way toward the velocity vector (`flightpath`, default 0.35, at most 12 deg). Default fov 50 (vertical; Unity's fov is vertical, so this is about 78 deg horizontal at 16:9).
- Head motion (`head`, 0-1, default 0.6): the view looks into the turn from the filtered body rates (0.3 s lead, up to 8 deg yaw and 6 deg pitch) and sags a few cm under G. Both are smoothed over `head_smooth` (0.35 s).
- The aircraft is switched to its cockpit model with `Aircraft.SetCockpitRenderers(true)`, which also hides the exterior parts around the cockpit, as the game's cockpit camera does. The game's cockpit overlay camera (`cockpitCamRender`) is enabled with the same lens through its projection matrix, because NOVR blocks the fov setter. The pilot is hidden (`pilot` default false for `pov`). The hull check is off. Everything is restored when the shot ends.
- `pov_shoulder`: inside the canopy behind one shoulder, pilot visible. The first placement (eye + (0.22, 0.16, -0.5)) sat behind the ejection-seat headrest on the A-19 (`rejected/04_...`). It was moved to eye + (0.2, 0.2, -0.3) with fov 58. That version has not been rendered (renders stopped for Tal's live session).
- HUD and MFDs: not available in replays. `Cockpit` creates the MFD (`TacScreen`) only when `CombatHUD.aircraft` is the aircraft, i.e. the local player's own jet, and the HUD (`FlightHud`, `CombatHUD`, `HUDAppManager`) is driven by the local aircraft too. A replay has no local aircraft, so the screens render black and the joystick and throttle do not move. `hud: true` hands the target to `FlightHud` and keeps its canvases enabled; it showed nothing in the first render. `cockpit_canvases` keeps world-space canvases under the aircraft enabled (there were none to keep). Diagnostics go to `<game>/NuclearReplay/traces/pov_diag.txt`. The `-ncNoHud` canvas hiding is not the cause.
- NOVR: it blocks `Camera.fieldOfView` (handled as for the other cine cameras) and reparents `cockpitRenderer` under "NOVR Main Camera", which the code handles. No other effect was seen.

### OBS VR footage: levelled, re-centred crop

`fx/camera/tools/vrstab.py <obs_start> <dur> <out.mp4> [--zoom 1.3] [--cx 0.13] [--cy -0.06] [--roll 1] [--sigma 0.3]` (run with the `depth-title/.venv` Python). It measures the panel roll per frame from straight cockpit edges, rotates the frame to level the panel, zooms and shifts the window toward the HUD and MFD (to the right), and keeps the window inside the source. When the full de-roll does not fit at that zoom, it reduces the de-roll first and then slides the window. The path is smoothed. Audio is OBS a:1. The result is steadier and more level, but it cannot show what the eye did not see: when Tal looks left, the forward view is not in the source. A head-motion lock from cockpit feature tracking (`vrtrack.py`) was tried and dropped, because it drifts onto dark terrain (b_cock). `ba_sheet.sh` makes before/after sheets.

### Demo (`fx/camera/demo/pov/`)

Replay POV clips (1920x1080, 30 fps, OBS game audio at replay time; rendered with `pitch` -6 before the default changed to 0), each with `_sheet.jpg` and `_ba.jpg` (top: the OBS view at the same moment; bottom: the replay POV):

- `01_replay_pov_scythe_launch_1743.5-1751.5`: Scythe launch over the desert hills; the missile smoke crosses the canopy.
- `02_replay_pov_lowrun_corvette_1981-1989`: low over the sea toward the burning corvette, banking hard at the end.
- `03_replay_pov_low_terrain_agm_2394-2401`: A-19 Brawler low over green hills with AGM smoke ahead, framed by the two-pane canopy.

OBS stabilised clips in `obs_stab/` (`obs_<v3 shot>_stab.mp4` + `_ba.jpg`): `c_cock2` (1903, low run) and `l_dive` (5884.5) with `--cx 0.13 --cy -0.06 --zoom 1.35`; `w_cock2` (1706) and `b_cock` (4865.5) with `--cy -0.08 --zoom 1.3`.

Rejected: `rejected/00_game_cockpit_camera_1700-1704` (the game's cockpit camera) and `rejected/04_replay_pov_shoulder_v1_turn_2250-2257`.

### Still to render

`fx/camera/pov2/shots.json` was queued and then cancelled before the game started. It covers `pov` with the new defaults (pitch 0, plus `pitch` 3 on the low run) at 1743.5-1751.5 and 1981-1989, `pov` in the jink turn at 2250-2257, and the re-tuned `pov_shoulder` at 2394-2401. Run:

```
scripts/render-clips.sh "<demo>.ncdemo" "1743.5:1751.5,1981:1989,2250:2257,2394:2401" fx/camera/pov2/clips fx/camera/pov2/shots.json
```

The installed plugin DLL already contains this code (backup `NuclearContext.Replay.dll.orig-2026-10-03b` in `dist/` and the plugin folder).

### Known limits (POV)

- No HUD and no MFD content in replays (see above). Overlaying an honest HUD from telemetry (speed, altitude, heading) in post would be possible, but it was not built.
- The stick and throttle do not move, and cockpit warning lights follow whatever state the remote aircraft has.
- `head` motion and `pitch` 0 have not been seen in a render yet.
- The OBS crop cannot recover the forward view when Tal looks away. It loses resolution at 1.3-1.35x zoom, and fast head roll is only partly removed.
