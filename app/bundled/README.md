# Bundled plugin and loader files for `nr install`

`nr install` looks for these files in a `bundled` folder next to the `nr` executable
(override with the `NC_BUNDLED_DIR` environment variable, or pass `--from <dir>` and
`--bepinex <zip>`). Nothing here is committed: the repository `.gitignore` excludes
`*.dll`, `*.zip` and `*.exe`. Releases do not ship `nr`; build it from source
(see docs/DEVELOPMENT.md) and fill this folder yourself if you want the defaults.

```
bundled/
  BepInEx_win_x64_5.4.23.5.zip   BepInEx 5 for Windows x64 (also used under Proton), official release, unmodified
  plugin/
    NuclearReplay.dll           the mod (recorder + replay/render plugin in one DLL), from scripts/build.sh
```

- BepInEx: https://github.com/BepInEx/BepInEx/releases. The BepInEx code is MIT (the
  LICENSE file at tag v5.4.23.5); the Unity Doorstop loader inside the zip (`winhttp.dll`)
  is LGPL-2.1.
- ffmpeg is not bundled. It is pinned (URL, size, SHA-256) in `shared/Ffmpeg/FfmpegPackage.cs`,
  which both `nr` and the replay plugin compile, and downloaded on request into
  `<game>/NuclearReplay/tools/ffmpeg/` (only `ffmpeg.exe` and `LICENSE.txt` are kept).
  `nr install`, `nr ffmpeg install` and the in-game "Clip this" prompt all use that one
  location. The build is the Windows one on Linux too: the render plugin runs inside the
  game under Proton (a Linux ffmpeg started through Wine cannot open the Windows paths it is given).
- `nr install` writes the ffmpeg path (the user setting, else the shared location, as a
  game-side path) and the clips folder into the replay plugin's config when those keys are empty.

`nr install --from <dir> --bepinex <zip>` and `nr settings set ffmpeg_path <file>` override
these locations for development.
