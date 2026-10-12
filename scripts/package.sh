#!/usr/bin/env bash
# Builds the release package:
#   dist/NuclearReplay-<version>-plugins.zip    NuclearReplay.dll (both plugins), LICENSE.txt and README.txt at the
#                                                zip root: the only release asset, for NOMM (NOMNOM) and installs by hand
#   dist/SHA256SUMS
#
# ffmpeg is not included. The replay viewer (and nr) download the build pinned in shared/Ffmpeg/FfmpegPackage.cs
# from its publisher when the player asks; README.txt names that pin.
# nr, the command line tool, is not packaged; it is built from source (docs/DEVELOPMENT.md).
#
# Usage: scripts/package.sh
#
# Environment:
#   GAME_DIR          Nuclear Option folder with BepInEx installed. The plugins compile against its
#                     managed DLLs (nothing is written there). Default: the Steam library on /mnt/games.
#   SOURCE_DATE_EPOCH timestamp for every zip entry. Default: the last commit's time, so the zip
#                     layout and metadata are reproducible.
#
# Version: version.props (NcVersion) is the only place it is set.
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
game_dir="${GAME_DIR:-/mnt/games/SteamLibrary/steamapps/common/Nuclear Option}"
dist="$project_dir/dist"
build="$dist/build"

version="$(sed -n 's:.*<NcVersion>\(.*\)</NcVersion>.*:\1:p' "$project_dir/version.props")"
[[ -n "$version" ]] || { echo "NcVersion not found in version.props" >&2; exit 1; }
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$project_dir" log -1 --format=%ct 2>/dev/null || date +%s)}"

# ---- plugins, built from source against the game's managed assemblies ----
test -f "$game_dir/NuclearOption_Data/Managed/Assembly-CSharp.dll" || { echo "Game not found at $game_dir (set GAME_DIR)" >&2; exit 1; }
test -f "$game_dir/BepInEx/core/0Harmony.dll" || { echo "BepInEx is not installed in $game_dir; the plugins compile against it" >&2; exit 1; }
common=(-c Release --nologo -v q -p:ContinuousIntegrationBuild=true -p:Deterministic=true)
echo "build   plugins $version"
dotnet build "$project_dir/NuclearReplay.csproj" "${common[@]}" "-p:GameDir=$game_dir" -o "$build/plugins"
for dll in NuclearReplay.dll; do
    grep -aq -- "$version" "$build/plugins/$dll" || { echo "$dll does not carry version $version" >&2; exit 1; }
done

# ---- zips ----
python3 - "$project_dir" "$build" "$dist" "$version" <<PY
import hashlib, os, sys, time, zipfile
from pathlib import Path

project, build, dist = map(Path, sys.argv[1:4])
version = sys.argv[4]
epoch = int(os.environ["SOURCE_DATE_EPOCH"])
stamp = time.gmtime(max(epoch, 315532800))[:6]   # zip dates start in 1980
pkg = project / "packaging"

# The ffmpeg the viewer downloads on request: read from the one place it is pinned.
import re
pin_src = (project / "shared/Ffmpeg/FfmpegPackage.cs").read_text(encoding="utf-8")
def pin(field):
    m = re.search(field + r' = (?:"([^"]*)"|(\d+)),', pin_src)
    if not m:
        sys.exit(f"FfmpegPackage.Pinned has no {field}")
    return m.group(1) if m.group(1) is not None else m.group(2)

subst = {
    "@VERSION@": version,
    "@FFMPEG_VERSION@": pin("Version"),
    "@FFMPEG_URL@": pin("Url"),
    "@FFMPEG_SHA256@": pin("Sha256"),
    "@FFMPEG_SIZE_MB@": str(round(int(pin("Size")) / 1e6)),
    "@FFMPEG_PUBLISHER@": pin("Publisher"),
    "@FFMPEG_SOURCE@": pin("SourceUrl"),
}

def text(name, extra=None, crlf=False):
    s = (pkg / name).read_text(encoding="utf-8")
    for k, v in {**subst, **(extra or {})}.items():
        s = s.replace(k, v)
    if crlf:
        s = s.replace("\r\n", "\n").replace("\n", "\r\n")
    return s.encode("utf-8")

def info(arc, executable=False):
    zi = zipfile.ZipInfo(arc, date_time=stamp)
    zi.compress_type = zipfile.ZIP_DEFLATED
    zi.create_system = 3
    zi.external_attr = ((0o100755 if executable else 0o100644) & 0xFFFF) << 16
    return zi

# Nothing from the build machine may ship: home folders, the game drive, the owner's name, secrets.
# Checked in every text file and in the plugin DLL.
leak_words = ["/home/", "/mnt/", "/run/media/", "talamander", "swicegood", "secrets/", "update-signing.key"]
# The public handle may appear in these exact forms only: the copyright line, the plugin GUIDs (the
# old talamander.nuclearcontext* ones too: the DLL looks for them to migrate settings and to warn about
# an old install) and the GitHub repository.
public_name = ["copyright (c) 2026 talamander", "talamander.nuclearreplay.recorder", "talamander.nuclearreplay.player",
               "talamander.nuclearcontext", "github.com/talamander/nuclear-replay",
               "githubusercontent.com/talamander/nuclear-replay"]

def check_leaks(arc, data):
    if not arc.endswith((".txt", ".md", ".dll")):
        return
    for enc in ("utf-8", "utf-16-le"):
        low = data.decode(enc, errors="ignore").lower()
        for allowed in public_name:
            low = low.replace(allowed, "")
        for word in leak_words:
            if word in low:
                sys.exit(f"{arc} contains '{word}'. Remove it before releasing.")

def write_zip(out, root, entries, executables=()):
    entries.sort(key=lambda e: e[0])
    for arc, src in entries:
        if isinstance(src, bytes):
            check_leaks(arc, src)
        elif isinstance(src, Path) and src.is_file():
            check_leaks(arc, src.read_bytes())
    part = out.with_suffix(".zip.part")
    with zipfile.ZipFile(part, "w", compresslevel=9) as zf:
        for arc, src in entries:
            zi = info(root + arc, executable=arc in executables)
            if isinstance(src, bytes):
                zf.writestr(zi, src)
            else:
                if not src.is_file():
                    sys.exit(f"missing {src}")
                with open(src, "rb") as r, zf.open(zi, "w") as w:
                    while chunk := r.read(1 << 20):
                        w.write(chunk)
    os.replace(part, out)
    print(f"wrote   {out} ({out.stat().st_size / 1e6:.1f} MB)")

def crlf(data):
    return data.replace("\r\n", "\n").replace("\n", "\r\n")

# Plugins only, everything at the zip root: what NOMM extracts into BepInEx/plugins/<mod id>/ (the folder
# ModInstaller.PluginFolder names, so nr and NOMM recognize each other's installs). The plugins need
# nothing besides the game and BepInEx 5; ffmpeg is downloaded on request.
plugin_entries = [
    ("NuclearReplay.dll", build / "plugins/NuclearReplay.dll"),
    ("LICENSE.txt", crlf((project / "LICENSE").read_text(encoding="utf-8")).encode("utf-8")),
    ("README.txt", text("plugins-README.txt", crlf=True)),
]
write_zip(dist / f"NuclearReplay-{version}-plugins.zip", "", plugin_entries)

sums = []
for z in [dist / f"NuclearReplay-{version}-plugins.zip"]:
    sums.append(f"{hashlib.sha256(z.read_bytes()).hexdigest()}  {z.name}")
(dist / "SHA256SUMS").write_text("\n".join(sums) + "\n")
print("wrote   " + str(dist / "SHA256SUMS"))
PY
