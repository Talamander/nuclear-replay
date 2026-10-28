# Lighting: time of day, haze, weather and backlight in replays

Replay-time lighting control: the viewer's Lighting panel (F2) and the shot list's lighting fields. It is
cosmetic and lives only in the replay plugin. Nothing here is in the recorder, and nothing changes what the
recording says happened.

Code: `replay/src/Lighting.cs` (the override), `ShotLighting.cs` (settings, panel presets and text),
`BacklightMath.cs` (geometry), `CineBacklight.cs` (backlit cine cameras), `ReplayPlugin.Lighting.cs` and
`ViewerUi.Lighting.cs` (the panel); wiring in `Director.cs` and `CineCamera.cs`. Validation:
`app/src/NuclearReplay.Core/Shots/ShotLightingFields.cs`. Tests: `app/tests/NuclearReplay.Core.Tests/LightingTests.cs`
(no Unity runtime).

First built on the `lighting-lab` branch of the old nuclear-context repository (commit fb8821e, 2026-10-06), then
ported by hand and extended with weather, cloud height and the viewer panel (2026-10-28).

## How the game lights a scene

Everything is in `LevelInfo` (`NetworkSceneSingleton<LevelInfo>.i`). Sources: decompiled `LevelInfo`,
`CloudLayer`, `IRSeeker`, `Missile`, `BuildingLights`, `RGBCycler`, `NightVision` and `CameraStateManager`.

- State (SyncVars, written by the server, replicated to clients): `timeOfDay` (hours), `conditions` (weather,
  0 clear to 1 storm), `cloudHeight`, wind and `moonPhase`. The mission sets them through `LoadEnvironment`, and
  the server advances `timeOfDay` by `timeFactor`. In a replay the recorded server stream keeps writing them
  through `DeserializeSyncVars`.
- Sun position: the sun and moon lights are children of `worldAxis`. `UpdateTimeOfDayLighting` sets
  `worldAxis.eulerAngles = (x, y, timeOfDay * 15 + 180)`. `x` is `-latitude` from the map and `y` is never
  written after setup, so the sun's path across the sky is fixed per map. Only the time moves the sun along it.
  There is no separate sun elevation setting.
- `UpdateTimeOfDayLighting(bool forceImmediate)` runs from `LevelInfo.Update` on every peer. It is throttled to once
  per real second unless forced, and skips when the camera is below sea level. From `timeOfDay` it sets ambient
  light, fog color and density (`airDensity * 0.00014 * hazeAmount + cloudFog * 0.001`), the skybox, the sun color
  and bloom. The sun light switches off when its color goes black (after about 17.9 h on the HAW and
  Confrontation maps).
- `CloudLayer.UpdateWeatherSets` (every second) picks the weather set from `conditions`:
  `weatherSets[floor(conditions * count)]`. The stock sets are 0-0.2 Clear, 0.2-0.4 Scattered Cloud,
  0.4-0.6 Moderate Cloud, 0.6-0.8 Overcast and 0.8-1 Thunderstorm.
- `LevelInfo.DaylightCheck` turns building lights on at night. `NightVision`, `TargetCam`, `TacScreen` and the
  clocks read `timeOfDay` too.
- `IRSeeker.BackgroundBrightness` reads the sun. It is the only simulation reader of lighting.

## Why it cannot change what happened

The replay is a pure client: every unit is remote, so `Missile` runs `seeker.Seek()` (and with it
`IRSeeker.BackgroundBrightness`) only when `LocalSim` is true, which never happens in a replay. Positions, hits,
kills and damage come from the recorded server packets, which the override never touches. The recorder has no
lighting code, and replays never record themselves. In a live or hosted game the same override would matter (the
host's IR seekers read the sun), which is why it must never move to the recorder.

## How the override works

While any lighting key is active:

- `LevelInfo.timeOfDay`, `conditions` and `cloudHeight` hold the override, or the recorded value for a key left
  "as recorded". A Harmony prefix on `DeserializeSyncVars` puts the recorded values back before the stream's update
  is read; the postfix keeps what the stream wrote as the new recorded values and writes the override again. So
  every reader (sun, sky, clouds, building lights) follows the override, and "as recorded" writes back exactly what
  the recording says at that moment.
- A prefix on `UpdateTimeOfDayLighting` forces the update every frame and, for `sun_azimuth`, turns `worldAxis`
  about the world up axis (its `y` angle). The turn is undone when the azimuth key goes away.
- A postfix multiplies `RenderSettings.fogDensity` by `fog`, only when the game's update really ran (the prefix
  marks the call through the private `lightingLastUpdated`), so the multiplier never compounds.
- A weather change rebuilds the cloud layer's weather set at once instead of within a second.

Two layers merge key by key: the shot list's (set by the director for each shot, the only layer in `nr render`)
and the viewer panel's. The shot list wins. With no key in either layer nothing is patched and no lighting code
runs, so old shot lists render exactly as before.

## Shot list fields

All optional. On a shot, or in a root `"lighting": {...}` object as the default for every shot (a shot's key wins).
`nr shots validate` checks them (`ShotLightingFields.cs`).

| Field | Value |
|---|---|
| `time_of_day` | Hours, 0-24. Moves the sun along the map's path and drives sun color, sky, ambient and fog color with it. 6.5 dawn, 17.3 golden hour, 17.8 dusk, 22 night. |
| `sun_azimuth` | Compass degrees toward the sun (0 = +z, 90 = +x), or `"camera"`: the sun goes `sun_camera_off` degrees (default 15) right of the camera's view, locked at the shot's first frame. Elevation still comes from the time. |
| `sun_camera_off` | Degrees, -90 to 90, for `sun_azimuth: "camera"`. |
| `fog` | Multiplier on the game's haze (fog density): 0.25 clear air, 0.5 light, 2 hazy, 4 thick. |
| `weather` | 0 clear to 1 storm: cloud cover and overcast light (see the weather sets above). |
| `cloud_height` | Meters above sea level of the cloud layer. |

Backlight places a cine camera instead of moving the sun (shot keys only):

| Field | Value |
|---|---|
| `backlight` | `true` on `chase_cine`, `flyby`, `track_from` or `keyframe`: the camera goes on the far side of the subject from the sun and looks toward it. Other cameras ignore it (a validator warning). |
| `backlight_off` | Degrees between the sun and the subject seen from the camera, 0-90 (default 15). 10-20 keeps the sun at the frame edge of a 45 degree lens. |
| `backlight_side` | `"right"` (default) or `"left"`: the side of the subject the sun is on. |
| `backlight_pitch` | Degrees, -89 to 89; positive puts the sun higher in the frame. |
| `backlight_at` | `track_from`: seconds ahead at which the subject crosses the sun line (default 2). |
| `backlight_height` | `chase_cine`: meters added straight up. |
| `backlight_min` / `backlight_max` | Meters, bounds of the ground search along the sun line (default 60 / 8000). |

Per camera: `chase_cine` gets a sun-locked offset `distance` m on the anti-sun side whatever the heading
(`look_smooth` defaults to 0.05 s). `track_from` with `"from": "backlight"` (or no `from`) stands `distance` m
(default 1200) back on the view line; with `agl` it stands that high above the ground where the sun line meets the
terrain (the ground silhouette shot). `flyby` puts its spot `offset` m on the anti-sun side. `keyframe` relative
keys use a sun frame (+z toward the sun). Backlight reads the sun after the same shot's override, so
`time_of_day` and `backlight` combine.

Example (golden hour, backlit chase):

```json
{ "lighting": { "time_of_day": 17.3 },
  "shots": [ { "t": 15, "camera": "chase_cine", "target": "unit:11706", "fov": 45, "backlight": true } ] }
```

## The viewer's Lighting panel (F2)

F2 opens the panel at the bottom right. Its keys work only while it is open:

| Key | Action |
|---|---|
| Y | Time: as recorded, dawn (6:30), golden hour (17:18), dusk (17:48), night (22:00) |
| PageUp / PageDown | Time 15 minutes later / earlier |
| N | Haze: as recorded, clear air, light haze, hazy, thick haze |
| V | Weather: as recorded, clear, cloudy, overcast, storm |
| Z | Sun behind subject: the free camera moves to the backlit side of the aircraft it follows (or the Camera Studio aircraft, or the nearest one), at the same distance (30-400 m), looking at it with the sun 15 degrees to the right. With a Camera Studio key selected, that key is updated too. |
| Home | Everything as recorded |
| F2 | Close the panel (the settings stay) |

- The settings stay through seeks, camera changes and cuts until the replay closes.
- Clips (C) record what the viewer shows, so they carry the light. The clip's `job.json` gets the panel's settings
  as its root `"lighting"`.
- Camera Studio saves the panel's settings with the path (`<recording>.path.json`: the saved shot carries the keys
  and the `studio` section keeps a `lighting` object). Opening the studio on that recording brings them back into
  the panel, and a clip of the path (C in the studio) records with that light. (`nr shots validate` does not yet
  know the studio's `path` camera, so `nr render --shots <recording>.path.json` is not a supported route today.)
- When a shot list also sets lighting, its keys win over the panel, and the panel says so.
- Test command (the `-ncCommands` file): `light on|off|next-time|next-haze|next-weather|behind|recorded`,
  `light time <hours|dawn|golden hour|dusk|night|recorded>`, `light haze <x|recorded>`,
  `light weather <0-1|recorded>`, `light cloud <m|recorded>`, `light azimuth <deg|camera|recorded>`. The status
  file has a `lighting` object (`open`, `viewer`, `effective`, `shot_list`, `recorded_time`, `describe`).

## Limits

- No sun elevation key. The elevation follows the time and the map's latitude. On HAW at 17.3 h the sun is at
  azimuth 262, elevation 7; at 17.8 h elevation 2. Golden hour is about 17.0-17.6 h, not 18. After about 17.9 h
  the sun light is off.
- "Sun behind subject" places the camera once; it does not follow. For a moving backlit shot, use a Camera Studio
  path or a shot list with `backlight`.
- `fog` scales only the distance haze on terrain and objects. The skybox haze and sun glow do not change, so it
  shows on terrain-heavy frames and little on sky-only frames.
- A weather override changes cloud cover and overcast light. Rain and lightning effects, if the map has them,
  were not checked.
- The override forces `UpdateTimeOfDayLighting` every frame while active (the game does it once a second). The
  cost is a few curve evaluations and material sets.
- Ground silhouette cameras at 2 m can stand inside trees (tree instances have no colliders). 20-30 m `agl` is
  safer.
- A low sun behind a jet seen from the ground needs a very long lens; at that focal length the sun's bloom erases
  the jet while it crosses the disc. `backlight_off` 2-4 keeps the sun just out of frame.
- Renders are not bit-reproducible run to run. "Old shot lists render as before" is checked by code path: with no
  lighting key the Harmony patch is never applied and no lighting code runs.

## Results

- 2026-10-06 (Confrontation, lab): backlit `chase_cine` at dawn, golden hour and dusk gives a dark jet, the sun in
  frame and warm rim-lit terrain. Backlight at a mid-morning sun looks up into a blue sky. `fog` shows only on
  terrain-heavy frames.
- 2026-10-28 (HAW demo, 16-21 s, alexandxor's FS-12): as recorded, golden hour backlit and dusk. Evidence in
  `/mnt/games/nuclear-option-content/2026-10-28-creator-tools/lighting/`.
