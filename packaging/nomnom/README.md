# NOMNOM entry (draft)

`NuclearReplay.json` is the draft entry for [NOMNOM](https://github.com/KopterBuzz/NOMNOM), the registry
that NOMM (Nuclear Option Mod Manager) installs mods from. It follows NOMNOM's `SCHEMA.md` and
`template.json` and passes its `ValidationSchema.json` and the content checks of `Validate-JsonContent.ps1`
(file name equals `id`, https URLs, archive file names, numeric versions). Nothing has been submitted.

## Before submitting

1. **The rename is done (0.5.0).** `id` `NuclearReplay` equals `ModInstaller.PluginFolder` and the plugin
   assembly name (`NuclearReplay.dll`, as NOMNOM asks), so NOMM, an install by hand and `nr install` all
   use the same `BepInEx/plugins/NuclearReplay/` folder. A player who had Nuclear Context must delete the
   old `BepInEx/plugins/NuclearContext/` folder by hand; the mod says so in the game while both are there.
   (`nr install` moves that folder to its backup folder by itself.)
2. **Publish the first public release** with the plugins zip as its only asset (`app/RELEASING.md`),
   tagged with a plain numeric version such as `v0.5.0`.
3. **Replace the placeholders** (every value starting with `PLACEHOLDER`): `fileName` and `downloadUrl` from
   the plugins zip asset of that release, and `hash` as `sha256:<64 hex digits>` from the release page (the
   "copy" button next to the asset). NOMM checks downloads against `hash`, so a placeholder there would
   fail every install.
4. Check `gameVersion` (the newest Nuclear Option version the release was tested with; `0.34.1` is the
   version in `docs/DEVELOPMENT.md`).
5. Copy the file to `modManifests/NuclearReplay.json` in a fork of NOMNOM and open a pull request to `main`.

After that, `autoUpdateArtifacts` `"True"` lets NOMNOM add new releases by itself: it reads the first
asset of each new GitHub release and turns the tag into the artifact version.

## Choices

- `isClientOrServer` `Both`: the recorder works where the match is hosted (single-player or a hosted
  multiplayer match); players who join do not need the mod. The replay viewer runs offline on the
  player's own PC.
- `tags`: `mod`, `Utility`, `Camera` and `multiplayer`, from the tags other NOMNOM entries use most.
- No `dependencies`: NOMM installs BepInEx 5 itself, and the plugins need nothing else. FFmpeg for clips is
  downloaded by the game on request.

## How nr and NOMM see each other's installs

Checked against NOMM's `Installer.kt`, `LocalMods.kt` and `RepoMods.kt`:

- NOMM unpacks the plugins zip into `BepInEx/disabledPlugins/<id>/`, writes `meta.json` (id and version)
  there and moves the folder to `BepInEx/plugins/<id>/`. The plugins zip has its files at the root, so
  the DLL lands where `nr` looks for it. `nr install` updates NuclearReplay.dll in place and leaves
  `meta.json` alone.
- NOMM treats a plugins folder without `meta.json` as the mod named like the folder. An install by hand
  or by `nr install` is therefore listed as that mod (with "update available", because NOMM does not know its version), and an
  update from NOMM replaces it cleanly.
- Turning the mod off in NOMM moves the folder to `BepInEx/disabledPlugins/<id>/`. `nr status` then says
  the mod is turned off in a mod manager.
