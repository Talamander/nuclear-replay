#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
game_dir="${1:-/mnt/games/SteamLibrary/steamapps/common/Nuclear Option}"
python3 - "$project_dir" "$game_dir" <<'PY'
import os
from pathlib import Path
import shutil
import time
import sys
project, game = map(Path, sys.argv[1:])
source = project / 'dist/NuclearReplay.dll'
if not (game / 'BepInEx/core/BepInEx.dll').is_file():
    raise SystemExit('Existing BepInEx 5 installation required; no loader is installed by this script.')
if not source.is_file():
    raise SystemExit('Build first: dist/NuclearReplay.dll is missing.')
target = game / 'BepInEx/plugins/NuclearReplay/NuclearReplay.dll'
target.parent.mkdir(parents=True, exist_ok=True)
if target.exists():
    shutil.copy2(target, project / 'dist/NuclearReplay.previous.dll')
temporary = target.with_suffix('.next')
shutil.copy2(source, temporary)
os.replace(temporary, target)
print(f'Installed {target}. A running game keeps its existing loaded plugin; next normal start loads this file.')
# NuclearReplay.dll holds both plugins. Before the rename the mod was BepInEx/plugins/NuclearContext
# (NuclearContext.dll, NuclearContext.Replay.dll); BepInEx would load both, so move that folder out of the
# game. Recordings in <game>/NuclearContext are not touched.
legacy = game / 'BepInEx/plugins/NuclearContext'
if legacy.is_dir():
    moved = project / 'dist/legacy-plugins' / time.strftime('%Y%m%dT%H%M%S') / 'NuclearContext'
    moved.parent.mkdir(parents=True, exist_ok=True)
    shutil.move(str(legacy), str(moved))
    print(f'Moved the old {legacy} to {moved}.')
PY
