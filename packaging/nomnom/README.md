# NOMNOM entry

`NuclearReplay.json` is the entry for [NOMNOM](https://github.com/KopterBuzz/NOMNOM), the registry that
NOMM (Nuclear Option Mod Manager) installs mods from. It follows NOMNOM's `SCHEMA.md` and `template.json`
and passes its `ValidationSchema.json` and the content checks of `Validate-JsonContent.ps1` (file name
equals `id`, id not taken, https URLs, archive file names, numeric versions). Submitted on 2026-10-12 as
[KopterBuzz/NOMNOM#433](https://github.com/KopterBuzz/NOMNOM/pull/433), now with 0.5.1 and 0.5.0. It is
waiting for review.

## Keep it right

- `id` `NuclearReplay` equals `ModInstaller.PluginFolder` and the plugin assembly name
  (`NuclearReplay.dll`, as NOMNOM asks), so NOMM, an install by hand and `nr install` all use the same
  `BepInEx/plugins/NuclearReplay/` folder. Never change the `id`.
- `artifacts` is newest first. NOMNOM's hourly job (`Update-ModArtifact.ps1`) compares each GitHub
  release with `artifacts[0]` only, so an older version first would make it add the newer releases again.
- With `autoUpdateArtifacts` `"True"`, that job adds each new release by itself. It reads the **first**
  asset of the release and turns the tag (`v0.5.2` becomes `0.5.2`) into the artifact version. Keep the
  tag numeric and equal to the DLL version, upload `NuclearReplay-<version>-plugins.zip` first
  (`app/RELEASING.md`), and do not open NOMNOM pull requests for new releases.
- `gameVersion` is copied from `artifacts[0]` to each new release. Change it with NOMNOM's issue template,
  not with a pull request.

## Choices

- `isClientOrServer` `Both`: the recorder works where the match is hosted (single-player or a hosted
  multiplayer match); players who join do not need the mod. The replay viewer runs offline on the
  player's own PC.
- `tags`: `mod`, `Utility`, `Camera` and `multiplayer`, from the tags other NOMNOM entries use most.
- `imageUrl` and `imageHash`: the icon, `docs/media/icon-512.png` (PNG, 512x512, NOMNOM's limit), as a
  raw.githubusercontent.com URL pinned to a commit so the file behind it never changes. `imageHash` is the
  file's SHA-256 as plain lowercase hex with no `sha256:` prefix: NOMM's `PreviewImageLoader.kt` compares it
  with the downloaded bytes and shows no icon when they differ, and NOMNOM's image issue template writes the
  same format. To change the icon after the merge, use the "Update Mod Image URL" issue template
  (`HOWTO_UPDATE_MODIMAGE.md` in NOMNOM) with a new pinned URL.
- No `dependencies`: NOMM installs BepInEx 5 itself, and the plugins need nothing else. FFmpeg for clips is
  downloaded by the game on request: it asks first, then gets a pinned build (about 258 MB, GPLv3) from
  github.com and checks its SHA-256 before use.

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
