# Releasing

Nuclear Replay is free. There is no store, license key or paywall, and no website or server of our own:
releases live on GitHub (https://github.com/Talamander/nuclear-replay/releases).

A release is one zip: `NuclearReplay-<version>-plugins.zip`, with `NuclearReplay.dll` (both plugins),
`LICENSE.txt` and `README.txt` at the zip root. Players install it with NOMM or by hand. There is no
desktop app, no app zip and no update manifest: NOMM finds new releases by itself, and players who
installed by hand download the new zip from the releases page. `nr` is not released; developers build it
from source (`docs/DEVELOPMENT.md`).

(Until 2026-10-12 releases also carried Windows and Linux app zips and a signed `manifest.json` for the
app's updater. Both were removed with the app. `secrets/update-signing.key` is no longer used by
anything.)

## Tags

NOMNOM, the registry NOMM installs from, finds new releases by itself: it strips `v` from the tag and parses
the rest as a `System.Version` (two to four numbers). A tag like `v0.5.0-beta.2` does not parse, so NOMM
would never offer that release. Use plain numeric versions for public releases (`v0.5.0`, `v0.5.1`), set
`NcVersion` in `version.props` to the same number first, and never mark the release as a pre-release. The
version inside the plugins (`[BepInPlugin]`, `AssemblyVersion`) is the numeric part of `NcVersion`, so it
matches the tag. The NOMNOM entry itself is in `packaging/nomnom/`; it was submitted as
[KopterBuzz/NOMNOM#433](https://github.com/KopterBuzz/NOMNOM/pull/433) and is waiting for review.

## Publishing a release

The examples use the GitHub CLI (`gh`); the web page works the same way.

1. Set `NcVersion` in `version.props`, add the `CHANGELOG.md` entry and commit.
2. Build the zip against a game folder with BepInEx installed (the plugin compiles against its DLLs;
   nothing is written there):

   ```sh
   GAME_DIR="/path/to/Nuclear Option" scripts/package.sh
   ```

   It writes `dist/NuclearReplay-<version>-plugins.zip` and `dist/SHA256SUMS`, and stops if any text
   file or the DLL contains a build-machine path, the owner's name or a secret.
3. Create the release as a draft, check it, then publish it:

   ```sh
   gh release create v0.5.1 --draft --title "Nuclear Replay 0.5.1" --notes-file release-notes.md \
     dist/NuclearReplay-0.5.1-plugins.zip
   gh release upload v0.5.1 dist/SHA256SUMS
   gh release view v0.5.1 --json assets --jq '.assets[0].name'   # must print the plugins zip
   gh release edit v0.5.1 --draft=false --latest
   ```

   The plugins zip must be the **first** asset: NOMNOM installs the first asset of each release into
   `BepInEx/plugins/<mod id>/`. Upload assets one call at a time; GitHub lists them in upload order.
4. First release only: fill in and submit the NOMNOM entry (`packaging/nomnom/README.md`). After that,
   NOMNOM adds new releases by itself.

The full checklist, with the checks to run before step 3, is in `docs/PRODUCT-STATUS.md` ("Release
checklist").
