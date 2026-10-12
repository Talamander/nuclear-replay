# Jump points: research

Status: research only (2026-10-10, ingame-viewer branch). Nothing here is built. The recorder format
is unchanged.

## The problem

The in-game viewer plays a recording by standing in for the original host (`ReplayServer`). It sends
the recorded Mirage messages to the real game client in recorded order and at recorded pace. The
client only knows the state that the messages so far have built, so a jump has two paths today:

- **Forward:** fast-forward from where the replay is. The game runs fixed-step at 8x replay speed
  (60 steps per second of game time) until it reaches the target.
- **Backward:** restart. The client disconnects (`NetworkManagerNuclearOption.Stop`), the mission
  loads again, and the replay fast-forwards from the start to the target.

Measured in the headless test copy (1920x1080, gamescope capped at 60 Hz, RTX 5090):

| Jump | Time |
| --- | --- |
| Forward 20 s to 300 s (Escalation, 330 s, multiplayer) | 33.6 s (about 8x) |
| Backward 300 s to 150 s (same recording) | 26.5 s |
| Restart and jump to 12 s (05. Furball) | about 7 s |
| Backward 88 s to 20 s (05. Furball) | 10 s |
| Timeline click to 60 % (05. Furball, forward 32 s to 50 s) | about 3 s |

Restarting costs about 5 to 7 s of mission load. Fast-forward costs about 1/8 of the distance. On a
30-minute match, a jump back to minute 25 would take about 3 minutes.

## What Mirage sends when a client joins

The recorder attaches an invisible spectator connection on the host. Mirage then treats it as a newly
joined remote client and sends it the full world. This join burst is the start of every recording.
So a jump point is "what Mirage sends a client that joins at time t".

Message counts and bytes measured from two recordings with the plugin's `msgstats` test command. The
join burst is the first 3 s after the first message. The rest is everything after it.

05. Furball (single player, 83 s, 10 aircraft):

| Message | Join count | Join bytes | Rest count | Rest bytes |
| --- | ---: | ---: | ---: | ---: |
| `SendTransformBatcher+TransformMessage` | 140 | 19 667 | 4 784 | 869 781 |
| `Mirage.RemoteCalls.RpcMessage` | 192 | 1 243 | 5 616 | 45 591 |
| `Mirage.SpawnMessage` | 160 | 16 120 | 35 | 3 172 |
| `Mirage.UpdateVarsMessage` | 62 | 1 750 | 1 013 | 13 331 |
| `NetworkMission+SyncMission` | 1 | 2 442 | 0 | 0 |
| `Mirage.ObjectDestroyMessage` | 0 | 0 | 26 | 78 |
| Auth, `LoadMapMessage`, `SyncMissionStart` | 3 | 44 | 0 | 0 |
| **Total** | | **41 266** | | **931 953 (11.6 KB/s)** |

Escalation (multiplayer, 330 s, recorded by mod 0.4.1):

| Message | Join count | Join bytes | Rest count | Rest bytes |
| --- | ---: | ---: | ---: | ---: |
| `SendTransformBatcher+TransformMessage` | 99 | 89 454 | 8 859 | 5 379 162 |
| `Mirage.RemoteCalls.RpcMessage` | 764 | 4 943 | 179 670 | 1 167 130 |
| `Mirage.SpawnMessage` | 1 090 | 99 852 | 176 | 16 827 |
| `Mirage.UpdateVarsMessage` | 131 | 9 205 | 4 309 | 77 282 |
| `NetworkMission+SyncMissionPart` (+ header, footer) | 42 | 36 940 | 0 | 0 |
| `BrotliCompression.StringStoreStrings` / `Lengths` | 2 | 5 096 | 0 | 0 |
| `Mirage.ObjectDestroyMessage` | 0 | 0 | 171 | 682 |
| **Total** | | **245 534** | | **6 641 083 (20.3 KB/s)** |

In order, a join sends:

1. `AuthSuccessMessage`, then `LoadMapMessage`. The client loads the map scene and answers
   `SceneReadyMessage`. The replay already waits for that answer.
2. The mission: `NetworkMission+SyncMission`, or for big missions `SyncMissionHeader`, about 40
   `SyncMissionPart` messages and a `SyncMissionFooter`. Then `SyncMissionStart`.
3. Mirage's string store for Brotli string compression (`StringStoreStrings`, `StringStoreLengths`).
   It is sent once per connection. Later messages refer to strings by index.
4. One `SpawnMessage` per networked object the client can see: aircraft, ground units, buildings,
   missiles in flight, players. Each carries the prefab, owner flags, position, rotation and the
   initial serialization of every `NetworkBehaviour` (all SyncVars and custom `OnSerialize` data).
   The average is about 92 to 100 bytes.
5. The current transforms (`TransformMessage` batches, several objects per message), the first
   `UpdateVarsMessage` deltas and RPCs as the game sends them.

**Snapshot size:** one jump point is about the size of the join burst. It is 40 KB for a small
single-player mission and 245 KB for a busy multiplayer mission. The mission sync (2 to 37 KB) and
the string store (5 KB) do not change during a match, so they can be shared. Per-snapshot cost is
then about 20 to 200 KB. That is 2 to 10 seconds of the normal stream.

## Ways to get jump points

### A. Synthetic jump points from the existing stream (recommended first)

Build snapshots offline from messages the recording already has, without the host:

- **Spawns:** keep every `SpawnMessage` sent before t whose object has no `ObjectDestroyMessage` before
  t.
- **SyncVars:** send every `UpdateVarsMessage` for those objects between their spawn and t, in order.
  They are deltas, so all of them are needed, but they are small (13 KB in 83 s, 86 KB in 330 s).
- **Transforms:** the `TransformMessage` batches from the last second before t give every moving
  object a current position and velocity to interpolate from.
- **Shared parts:** the mission sync and string store come from the start of the recording.

A jump is then a restart (mission load, about 5 to 7 s), the synthetic burst, and normal play from t.
It takes about the same time at minute 1 and at minute 30. The index (the message offsets for each
jump point) can be computed when the viewer opens a recording, or cached next to it in a side file
(`<recording>.ncjump`). The recording itself does not change, so **old recordings get jump points
too**.

### B. Recorder-written keyframes

Every N seconds the recorder asks Mirage to spawn the whole world to a second, throwaway ghost
connection and writes the messages as a keyframe. The state is then exact, including custom
`OnSerialize` data that the stream only has as deltas. Costs:

- At 30 s intervals that adds 40 to 245 KB per 30 s, about +12 % (Furball) to +40 % (Escalation) on
  file size.
- The host serializes up to about 1 000 objects in one frame. That is a possible hitch on a busy
  multiplayer host. It is not measured.
- It needs a recorder format change, so it is out of scope for this slice. The current player sends
  every record that is not an annotation as a message (`ReplayServer.Advance`). A new record kind
  would therefore need a format version bump, so that older mods refuse the file cleanly
  ("Unsupported demo format version"). Otherwise they would send keyframe bytes to the client. A
  side file avoids that.

### C. Keep fast-forward, make it faster

Fast-forward is capped by frame rate (8x at 60 Hz in gamescope). A visible window without vsync may
reach a higher multiple. Larger fixed steps risk physics and missile-guidance differences. This
helps forward jumps only. Backward jumps still restart from 0.

## Risks

- **RPC-only state.** RPCs carry most of the traffic (180 000 in the Escalation match). State that
  the game sets only through RPCs is not in a spawn payload or a SyncVar: damage and detached parts,
  countermeasure counts, radar locks, kill-feed lines, chat, and possibly gear and flaps. After a
  synthetic jump (A), an aircraft can look undamaged until the next RPC. Mitigation: per object,
  replay a filtered set of "sticky" RPCs (damage, part loss) between its spawn and t. This needs a
  list of RPC hashes from the game and is the main unknown.
- **Order inside the burst.** Spawns that refer to other objects (a missile's owner, a player's
  aircraft) need their targets first. Sending spawns in recorded order keeps that.
- **Missiles in flight.** A missile spawned before t is in the snapshot with its spawn-time state. Its
  guidance target and fuse are SyncVars or RPCs. Check that missile follow cameras pick it up.
- **Client prediction and effects.** Explosions, smoke trails and sounds that started before t do
  not exist after a jump. That is acceptable for a viewer.
- **Interest management.** The recorder is a spectator. If a mission limits what a spectator sees,
  the snapshot has the same limits as the recording. That is no worse than today.
- **Clock.** The replay answers pings with the recorded network time. After a jump the client must
  resync its clock to t before transforms interpolate. The current restart path already does this.
- **Host cost (B only).** A world spawn on the host in multiplayer is an unmeasured frame hitch.
- **Game updates.** Message ids are hashes of type names. A game update that renames or reshapes a
  message breaks old recordings for jump points and for normal playback alike.

## How old recordings fall back

Recordings without jump points (all recordings today, and new ones if A is not built) keep the
current behaviour:

- A forward jump fast-forwards from the current position at about 8x.
- A backward jump restarts the mission and fast-forwards from the start.
- The viewer shows "Jumping to m:ss (reloading the mission)..." while it works.

Approach A also gives old recordings jump points, because it only reads the existing stream. Under
approach B, recordings made before the format change use the fallback above, and recordings made
after it play only in mods that know the new version.

## Suggested next step

Prototype A behind a viewer setting:

1. Build the jump index on open (one pass over the records; for the 6.9 MB Escalation file this is
   well under a second).
2. On a backward jump, or a forward jump of more than about 60 s, restart and send the synthetic
   burst instead of fast-forwarding.
3. Compare screenshots at the same t with a fast-forwarded replay: aircraft damage, missiles in
   flight, the kill feed, and the scoreboard.
4. If damage differs, list the RPCs involved and add them to the burst.
