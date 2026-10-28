# Cinema plan: scrubbing, camera paths, camera feel

Status (2026-10-28, creator tools): any unit as a camera target (unit list on B, `type:` selector), the
`ride` (ride-along, keys 0 and R) and `two_shot` (key 9) cine cameras are built, unit tested and rendered
headless (`replay/src/CineRide.cs`, `CineTwoShot.cs`, `CameraFraming.cs`, `UnitList.cs`,
`ReplayPlugin.Cameras.cs`, `ViewerUi.Units.cs`). Camera Studio keys stay pose-only: a ride-along is
not a studio key; the studio's target space is the nearest equivalent.

Status (2026-10-23): step 1 (Camera Studio v1 and the shake stack) is built and unit tested, and not
yet checked in game. The path maths (`replay/src/CameraPath.cs`), the shake layers
(`replay/src/CameraShake.cs`), the `path` cine camera (`CinePath.cs`), the studio model
(`CameraStudio.cs`) and the viewer side (`ReplayPlugin.Studio.cs`, `ViewerUi.Studio.cs`) are written.
Not in v1: per-segment ease, Bezier handles, roll and focus keys, look-at-a-point aim, a key track
you can drag, and loop preview. It builds on [JUMP-POINTS.md](JUMP-POINTS.md),
which covers faster seeking in more depth.

## Goal

Make Nuclear Replay the tool people use to make cinematic Nuclear Option content. That needs three
things the mod does not do well today:

1. **Scrubbing.** Move through a recording freely, backward and forward, quickly enough to set up a
   shot.
2. **Camera paths.** Fly a camera along a spline, through a city or around a fight, that you lay
   down in the game and can edit.
3. **Camera feel.** Shake, lens and speed effects driven by what happens in the world, so less of
   the work has to be faked in post.

## What NOReplay teaches

[NOReplay](https://github.com/Klawifiantix/NOReplay) records a pose track: where every unit is, about
5 times a second. It plays the battle back with puppets that follow those tracks. Because it has
every pose at every time, it can scrub instantly and play in reverse. The cost is fidelity. Missiles,
gunfire and damage are acted out, and every airframe needs its own fixes.

We should keep the network replay as the real thing and add a pose track **beside** it, not instead
of it. The pose track gives us what NOReplay has (instant scrub, trails, Tacview export). The
network replay still gives us real missiles, damage, effects and kills for anything that gets
rendered. Video editors work the same way: scrub on light proxies, render from the real media.

Do not copy: puppet playback as the main mode, manual F8 recording, or the missing license.

## Pillar 1: scrubbing

### 1a. Jump points (constant-time seek)

See JUMP-POINTS.md, approach A. A jump rebuilds what Mirage sends a client that joins at time t,
from the existing stream. Any jump then costs about one mission load (5 to 7 s), whether it goes to
minute 1 or minute 30. Today a jump back to minute 25 of a 30-minute match takes about 3 minutes.
Old recordings get jump points too. The main risk is state that only RPCs carry (damage, lost
parts).

### 1b. Pose track

A side file, `<recording>.ncpose`: for each unit, its type, its spawn and despawn times, and its
position, rotation and velocity at 10 to 20 Hz. It also holds the times of shots fired and of
explosions. About 1 MB per 10 minutes of a busy match, compressed.

Two ways to get it, both without changing the `.ncdemo` format:

- **Recorder writes it.** The host has every unit, so new recordings sample transforms as they
  record.
- **Viewer builds it.** For old recordings, the first playback (or a background fast-forward pass
  at about 8x) samples the game's own units. That avoids decoding the packed
  `SendTransformBatcher` format ourselves.

The pose track unlocks trails (flight paths drawn in the world, like NOReplay), Tacview ACMI export
(`nr export-acmi`), the 2D map in the viewer and the `nr` CLI, and proxy scrub.

### 1c. Proxy scrub

While the timeline is being dragged, or while editing a camera path:

1. Pause the network feed and hide the real units.
2. Show lightweight proxies: the unit's visual meshes only, with no scripts or physics, posed from
   the pose track. Draw trails. Missiles become a model plus a smoke line.
3. Scrubbing is instant in both directions, at any speed, including reverse play.
4. On release, jump the real replay to that time (1a) and resume. Renders always use the real
   replay.

Risks: getting a visual-only copy of each unit type (from the game's unit definitions, or by
cloning the renderers of a live unit) and keeping floating origin correct for proxies.

## Pillar 2: Camera Studio (camera paths)

An in-game editor for camera paths, built on the existing `keyframe` cine mode (Catmull-Rom with
smootherstep easing). Extend that mode; do not replace it.

- **Laying keys:** fly the free camera and press K to drop a key. A key stores position, rotation,
  focal length, roll, focus distance and time. Keys and the path are drawn in the world.
- **Editing:** select a key, move it with the free camera, retime it on a key track under the
  timeline, and set the ease per segment. Preview plays the path in a loop.
- **Path maths:** centripetal Catmull-Rom (no overshoot or loops at sharp corners), with optional
  Bezier handles. The camera can move at constant speed (reparametrised by arc length) with a speed
  curve on top, or hit each key at its time.
- **Aim:** keyed rotation (quaternion spline), look at a unit, look at a point, or look along the
  path.
- **Space:** world (city b-roll, flyovers), relative to a unit (orbit a jet in flight), or mixed: the
  path in world space and the aim on a unit (a camera on a crane that tracks a passing jet).
- **Saving:** paths save into the shot list JSON, so the `nr` CLI, agents and presets can use them
  and a path can be reused on another recording of the same map.

**Location scout.** City b-roll often needs no battle at all. Ship or generate a short empty
recording per map (one blank mission on each map, recorded once), so any map opens straight into
Camera Studio. Time of day and weather control would make this much stronger. Feasibility is not
checked yet.

## Pillar 3: camera feel

- **Shake stack.** Generalise the Perlin shake in `CineAction.cs` into layers that any camera mode
  can use: handheld, vehicle rumble, wind buffet, each with amount and frequency.
- **Event shake.** Shake driven by real events in the replay: an explosion near the camera, with
  the hit arriving after a delay of distance over 343 m/s. A nuclear shockwave, a sonic boom as a jet
  passes, gunfire for a camera mounted on the aircraft. Post-production cannot do this easily,
  because post does not know where every explosion is. The mod does.
- **Lens.** The game uses URP, so a camera-local Volume can add depth of field (rack focus keyed on
  the path, or auto-focus on the target), motion blur, chromatic aberration, lens distortion,
  vignette, film grain and Panini projection for very wide shots. Each can be switched per shot.
  Focal length in mm with sensor presets. Dolly zoom: keep the target the same size while the camera
  moves. Risks: priority against the game's own Volumes, and the NOVR projection matrix workaround
  in `ApplyFov`.
- **Speed ramps.** Time remap per shot, for example slowing into an impact. The replay server is
  ours, so slowing the feed gives real slow motion with no frame interpolation. The viewer already
  plays from 0.25x to 4x.

## Pillar 4: render quality

- **Lockstep render.** Today "Clip this" records in real time. A lockstep mode advances replay time
  by exactly one frame per rendered frame (`Time.captureDeltaTime` plus the server clock). Renders
  can then use settings or resolutions (4K, 8K) the game cannot run at 60 fps.
- **Real motion blur.** With lockstep, render several subframes per output frame and average them.
  That gives true film motion blur, at a render-time cost.
- **Camera export for post.** Write the rendered camera track (JSON, plus a Blender or After Effects
  import script) together with the aircraft matte (`EngineMatte.cs` exists) and a depth pass.
  Editors can then add titles and effects that stick to the world.

## Order

| Step | What | Why first | Risk |
| --- | --- | --- | --- |
| 1 | Camera Studio v1: keys in game, world and unit space, preview, save to shot list. Shake stack. | Biggest creator value. Needs no seek breakthrough. | Low |
| 2 | Lens pack (URP Volume), focal mm, speed ramps, event shake | Visible quality jump. Builds on step 1. | Medium (Volume priority) |
| 3 | Jump points prototype (JUMP-POINTS.md approach A) | Seeks take 5 to 7 s anywhere | Medium (RPC-only state) |
| 4 | Pose track, trails, ACMI export | Unlocks proxy scrub and the map | Low to medium |
| 5 | Proxy scrub | Instant scrub and reverse | Medium (visual-only copies) |
| 6 | Lockstep render, accumulated motion blur, camera export | Final-quality output | Medium |
| - | Location scout maps | City b-roll without a battle | Low, if blank recordings work |

## To do from play testing

Found by Tal on 2026-10-23 with 0.5.1 in the viewer.

- **"Cinematic chase" looks the same as "Chase".** Done (unreleased): camera 1 and the
  `cinematic_chase` style now use `chase_cine`, a smooth low-side chase; camera 4 stays the game's
  chase camera.
- **Cockpit view has no instruments or copilot.** Done (unreleased), `replay/src/CockpitView.cs`:
  while the cockpit camera is on an aircraft the replay builds its tactical display, keeps the
  aircraft's own canvases on when the UI is hidden, and shows every crew member except the one in
  the camera's seat. The display was checked headless; the back-seater could not be, because the
  test copy cannot turn the cockpit camera around. Check it in a two-seater with head tracking.
- **Flares missing in replays.** Not fixed yet. Headless checks on 2026-10-24 with temporary
  logging (`FlareDiag`, removed again) found that the replay does fire and draw flares whenever the
  recording says the countermeasure trigger is on with the flare station picked: 05. Furball and
  the HAW client recording show flares for many aircraft. For Talamander in the HAW recording every
  trigger arrived with station 1, which is "Radar ECM" (stations: IR Flares, Radar ECM), so the
  replay jammed instead of flaring. The missiles fired at him there (AAM-36 Scimitar) are radar
  guided, so that may be right. Not yet checked: the IR shots at him later (AAM-29 at 881 s,
  IRM-S2 at 1178 s and 1319 s); seeking that far into a 34 minute recording took longer than the
  session allowed. Next: replay those windows following Talamander with the trigger and station
  logged; if the trigger is on station 0 there and no flares draw, the bug is in drawing; if no
  trigger arrives, short taps are being lost (a press shorter than Mirage's 0.1 s sync interval
  turns on and off between two syncs, so no observer ever sees it), and the fix is a recorder
  event per flare ejection that the replay plays back. Also ask Tal which recording and moment
  showed it.

## Open questions

- Can a map load with an empty mission in the viewer, or is a short blank recording per map the
  simplest way?
- Can time of day and weather be set on the client during a replay?
- Does a camera-local URP Volume win over the game's global Volume without side effects?
- Which RPCs carry damage and lost parts (the main jump-point risk)?
