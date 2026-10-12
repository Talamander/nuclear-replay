#!/usr/bin/env python3
"""Send the configured marker key directly to a focused Nuclear Option window on Hyprland.

This bypasses compositor shortcuts without changing bindings. It does not establish
alignment: review the rendered marker's first frame in the actual recording.
"""
import argparse
import json
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--key', choices=[f'F{i}' for i in range(1, 13)], default='F8',
                    help='Must match the running plugin key. Use F9 for the earlier 0.1.0 session.')
args = parser.parse_args()
try:
    active = json.loads(subprocess.check_output(['hyprctl', 'activewindow', '-j'], text=True))
    if active.get('class') != 'steam_app_2168680':
        raise ValueError('Focus the game first; no key was sent.')
    # The key is restricted to the explicit choices above, not arbitrary Lua input.
    expression = ('local w = hl.get_window("class:steam_app_2168680"); '
                  'if w then return hl.dispatch(hl.dsp.send_shortcut({mods="", '
                  f'key="{args.key}", window=w' + '})) end')
    subprocess.run(['hyprctl', 'eval', expression], check=True)
    print('Marker key delivered. Confirm a sync_marker event and the actual captured marker frame.')
except (OSError, ValueError, subprocess.CalledProcessError) as error:
    parser.exit(2, f'Error: {error}\n')
