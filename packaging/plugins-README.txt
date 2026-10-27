NUCLEAR REPLAY @VERSION@
========================

This zip holds Nuclear Replay, a free BepInEx 5 mod for Nuclear Option.
Mod managers (NOMM) install this same zip.

  NuclearReplay.dll   The whole mod: the recorder (it records the matches
                      you host and join) and the replay viewer ("Replays" in the
                      main menu, and "Clip this").
  LICENSE.txt         Zero-Clause BSD license (0BSD).


INSTALL BY HAND
---------------
1. Install BepInEx 5 (x64) into the Nuclear Option folder, if it is not
   there yet. On Linux, also set the Steam launch option
   WINEDLLOVERRIDES="winhttp=n,b" %command%
2. Make the folder <Nuclear Option>/BepInEx/plugins/NuclearReplay and put
   NuclearReplay.dll in it. Use exactly this folder name: NOMM looks for
   the mod there, so it can update it later.
   If you had the mod under its old name, Nuclear Context, delete the
   folder <Nuclear Option>/BepInEx/plugins/NuclearContext. Your recordings
   in <Nuclear Option>/NuclearContext stay where they are and still show.
3. Start the game. Host or join a mission to record it; recordings are saved in
   <Nuclear Option>/NuclearReplay/demos. Click "Replays" in the main menu
   to watch them.


CLIPS NEED FFMPEG
-----------------
FFmpeg, the free video tool that encodes clips, is not in this zip. The
first time you press C ("Clip this") in a replay, the game asks before it
downloads FFmpeg @FFMPEG_VERSION@ for Windows (about @FFMPEG_SIZE_MB@ MB) from
@FFMPEG_URL@
It checks the download (SHA-256 @FFMPEG_SHA256@)
and keeps only ffmpeg.exe and its license (GPL v3), in
<Nuclear Option>/NuclearReplay/tools/ffmpeg. FFmpeg is a separate program
that the game starts; it is not part of Nuclear Replay.

Clips are saved in <Nuclear Option>/NuclearReplay/clips.


MORE
----
Source code, help and bug reports (attach BepInEx/LogOutput.log):
https://github.com/Talamander/nuclear-replay

Nuclear Replay is a fan-made mod. It is not made or supported by
Shockfront Studios.
