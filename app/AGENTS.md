# nr and NuclearReplay.Core: notes for agents

This folder holds the command line tool `nr` and the library it sits on, `NuclearReplay.Core`.
The contract with the in-game plugin is `docs/PRODUCT-CONTRACT.md` at the repository root. Read it
before changing job files, shot lists or demo parsing.

`nr` is a developer and automation tool. It is not in the release zip; build it from source
(`docs/DEVELOPMENT.md`). Players use the in-game viewer. Prefer `nr ... --json` when you automate, and
read `docs/AGENTS.md` for the commands.

The desktop app (Avalonia) was removed on 2026-10-12. Its source is archived outside this repository
(`docs/PRODUCT-STATUS.md`, Decisions).

## Layout

```
app/
  NuclearReplay.slnx
  Directory.Build.props        net10.0, single-file self-contained publish when -r is given
  src/NuclearReplay.Core      discovery, install, demos, shots, render jobs, settings
  src/NuclearReplay.Cli       nr (System.CommandLine)
  tests/NuclearReplay.Core.Tests   unit and in-process CLI tests (fake Steam and game dirs)
  schemas/                     shotlist.schema.json, render-job.schema.json
  bundled/README.md            what nr install looks for next to the executable
```

## CLI and JSON contract

The command reference, JSON output, exit codes, camera styles, shot lists, ranges and the agent
workflow are in `docs/AGENTS.md` at the repository root. Keep it accurate; change it in the same
commit as the code.

## Core building blocks

- `Clips/ClipSpec` + `ClipPlanner.Plan` turn a moment, style, format and options into ranges, shots
  and a render request. `nr render` and `nr shots make` both go through it.
- `ClipCommand.RenderArgs` / `Line` build the equivalent `nr render` line for a spec. Options at
  their defaults are left out.
- `ShotPresets` builds camera styles; `ShotPresets.Pair` matches a kill with the missile launch that
  caused it (same shooter, that target, up to 90 s before, a missile id). `DemoSummary.MissileHits`
  counts paired kills. The in-game viewer has its own copy (`replay/src/ViewerPresets.cs`).
- `ModInstaller.Install` + `ModInstaller.Describe` install the mod and describe what was done in plain
  sentences (`nr install` prints them). With `InstallOptions.Viewer`
  (`ViewerDefaults.For(settings, game)`) the install also fills the replay plugin's `[Viewer]`
  `FfmpegPath` and `ClipsDirectory` where they are empty (`ReplayConfig`).
- `RenderFailure.Advice` / `Fix` give the plain-language next step and the log paths for a failed render.
- `NcError(code, message, fix, invalid)` is the shared error type. `nr` maps `invalid` to exit code 2.
- Clip file names come from `shared/Clips/ClipNames.cs`, compiled into Core and the plugin.

## State and environment

- Settings (`settings.json`) live in `%APPDATA%\Nuclear Replay` on Windows and
  `~/.config/nuclear-replay` on Linux. Job files, logs and install backups live in
  `%LOCALAPPDATA%\Nuclear Replay` or `~/.local/share/nuclear-replay`.
- `NC_HOME=<dir>` puts all state under one folder (`<dir>/config`, `<dir>/data`). Use it for tests
  and for agents that need isolated state.
- `NC_BUNDLED_DIR=<dir>` points to the plugin and BepInEx zip `nr install` uses (a development ffmpeg
  in `ffmpeg/` there is still used as a last resort; see `bundled/README.md`). Without it `nr` looks
  for `bundled/` next to the executable.
- Log: `nuclear-replay.log` in `<data>/logs` (`Log` in Core; rolled at 1 MiB, two old files kept).
  `nr` logs its arguments and non-zero exit codes. `nr diagnostics` writes to `<data>/diagnostics`.

## Build, test, publish

```
cd app
dotnet build NuclearReplay.slnx
dotnet test NuclearReplay.slnx
dotnet publish src/NuclearReplay.Cli -c Release -r win-x64     # or linux-x64
```

Publishing with `-r` produces one self-contained executable (`nr`).

Version: `version.props` at the repository root (`NcVersion`) is the only place to change it.
`nr` and the mod (`NuclearReplay.csproj`) import it; `AppVersion.Current` and the generated
`NuclearReplay.BuildVersion` class read it.

Release zip: `scripts/package.sh` builds the plugin from source and writes
`dist/NuclearReplay-<version>-plugins.zip` plus `dist/SHA256SUMS`. See `RELEASING.md`.

## Rules for changes

- Tests use fake Steam and game folders in a temp directory. Never point tests or `nr install`
  at a real game folder unless the owner asks.
- `ModInstaller` refuses to run while the game is running, backs up replaced files and replaces
  them atomically. Keep it that way.
- Keep `--json` output stable. Add fields; do not rename or remove them.
- User-facing text is plain English. Every error carries a fix.
