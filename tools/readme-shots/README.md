# README screenshots and GIF

How the media in `docs/media/` was made. Everything here is test-only: it is not in `NuclearReplay.csproj`,
not in `app/NuclearReplay.slnx` and not in the `scripts/package.sh` whitelist, so it never ships.

## In-game shots: `drive.py`

`drive.py` runs the isolated test copy of the game headless through `scripts/launch-test-copy.sh` (no window on
the desktop) and drives the replay viewer through the plugin's existing test hook, `-ncViewerCommands` (see
`RunCommand` in `replay/src/ReplayPlugin.Viewer.cs`). The `shot` command takes a full-resolution PNG with Unity's
`ScreenCapture`, viewer overlay included.

```sh
D=tools/readme-shots/drive.py
python3 $D start                                  # prints the gamescope pid
python3 $D cmd menu-button "menu-pick 0" menu-watch
python3 $D wait "s['live'] and not s['seeking']" 240
python3 $D cmd "moment 21" "preset 2"
python3 $D shot 03-kill                           # -> $README_SHOTS_OUT/03-kill.png
python3 $D stop                                   # kills only the gamescope pid it started
```

Defaults: `README_SHOTS_WORK=/mnt/games/nuclear-replay-branding/readme/_work`,
`README_SHOTS_OUT=/mnt/games/nuclear-replay-branding/readme/raw`.

Commands take about half a second to land, so action stills came from bursts instead (below), with the best frame
picked by eye.

## Bursts and the GIF: `burst/`

`burst/ReadmeBurst.cs` is a separate BepInEx plugin that records the finished screen (game and overlay) as raw RGBA
frames, read back from the GPU without stalling the game. Build it and copy it into the test copy only while
taking pictures, then remove it again:

```sh
dotnet build tools/readme-shots/burst -c Release -o /tmp/readme-burst
mkdir -p "/mnt/games/nc-gui-test/Nuclear Option/BepInEx/plugins/ReadmeShots"
cp /tmp/readme-burst/ReadmeBurst.dll "/mnt/games/nc-gui-test/Nuclear Option/BepInEx/plugins/ReadmeShots/"
# ... drive.py start, then:
python3 $D burst gif1 11 15 960                   # name, seconds, fps, width
python3 $D burst-wait gif1
# when done: remove BepInEx/plugins/ReadmeShots from the test copy
```

Frames land in `$README_SHOTS_WORK/burst-<name>/frame-NNNNN.rgba` with `index.txt` and `done.txt`. Turn one into
a PNG, or a range into the GIF:

```sh
ffmpeg -f rawvideo -pix_fmt rgba -s 1920x1080 -i frame-00020.rgba 03-kill.png
for i in $(seq 21 116); do cat $(printf frame-%05d.rgba $i); done > clip.rgba
ffmpeg -f rawvideo -pix_fmt rgba -s 960x540 -r 15 -i clip.rgba \
  -vf "fps=10,split[a][b];[a]palettegen=max_colors=72:stats_mode=full[p];[b][p]paletteuse=dither=none:diff_mode=rectangle" \
  -loop 0 demo.gif
```

The README media came from the "05. Furball" recording (`20261005T210050Z`): the kill is moment 21 (0:33), the
missile launches around 0:16, and the GIF is 0:29 to 0:35 with the camera switched from cinematic chase to
victim cut.

The test copy runs muted, so a real clip there ends with "Clip saved without sound". For `game-clip` the clip
(0:14 to 0:22) was recorded for real, then `toast Clip saved: 05. Furball 0-14 to 0-22.mp4` showed the message a
player with sound sees, before the `shot`. Stop the game with `cmd quit` before `stop`: killing gamescope can leave
a `winedevice.exe` of the test prefix behind.

## Web copies

`docs/media/` holds JPEG copies at 1280 px wide and quality 85, the GIF, and the logo and icon PNGs. The PNG
masters are kept outside the repository, in `/mnt/games/nuclear-replay-branding/readme/`.
