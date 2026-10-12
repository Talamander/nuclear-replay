# Testing against an isolated game copy

This is for developers. It is not part of the release and does not ship.

`nr` can start the game through a wrapper instead of Steam (`NC_LAUNCH_WRAPPER` for
renders and pictures, `NC_VIEWER_WRAPPER` for viewer launches; see `docs/AGENTS.md`, "State and
environment"). Two wrappers for Linux live in `scripts/`:

- `scripts/launch-test-copy.sh` runs an isolated copy of the game (`NC_TEST_COPY`, holding
  `Nuclear Option/` and a Proton `pfx/`; the default is a folder on the owner's machine) inside
  headless gamescope with Proton, muted, with no window on the desktop. Agents and tests use it.
- `scripts/launch-test-copy-visible.sh` runs the same copy in a normal window with sound, for a person
  trying the in-game viewer.

Both hold `$XDG_RUNTIME_DIR/nuclear-context-game.lock` while the game runs, refuse the Steam install
and Steam prefixes, and need Steam running and signed in (Steamworks).

## Unit tests

```sh
dotnet test app/NuclearReplay.slnx
```

This runs the `nr` and `NuclearReplay.Core` tests (fake Steam and game folders, no game start).
They also cover the in-game viewer's Unity-free text and rules (`replay/src/ViewerText.cs`, linked
into the test project): the clip message, when the status line names a moment, and clip file names
(`shared/Clips/ClipNames.cs`).

The recorder tests under `tests/` are listed in `docs/DEVELOPMENT.md` (the Contracts project needs `dist/NuclearReplay.dll` from a recorder build).
