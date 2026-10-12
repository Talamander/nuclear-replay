#!/usr/bin/env python3
"""Summarize a Nuclear Replay network demo (.ncdemo): header, duration, message mix, annotations, events.

Usage: demo_inspect.py [--events] demo.ncdemo [...]
  --events   print only the annotation records, one JSON object per line, each with "t" (replay seconds,
             record time minus the first record's time, the time shot lists and render ranges use).
"""
import json
import struct
import sys
from collections import Counter

MAGIC = b"NCDEMO\x00"
KNOWN = [
    "Mirage.SpawnMessage", "Mirage.ObjectDestroyMessage", "Mirage.ObjectHideMessage", "Mirage.UpdateVarsMessage",
    "Mirage.RemoteCalls.RpcMessage", "Mirage.RemoteCalls.RpcWithReplyMessage", "Mirage.RemoteCalls.RpcReply",
    "Mirage.SceneMessage", "Mirage.SceneReadyMessage", "Mirage.SceneNotReadyMessage", "Mirage.AddCharacterMessage",
    "Mirage.RemoveCharacterMessage", "Mirage.RemoveAuthorityMessage", "Mirage.NetworkPingMessage", "Mirage.NetworkPongMessage",
    "Mirage.Authentication.AuthSuccessMessage", "Mirage.Authentication.AuthMessage",
    "Mirage.Serialization.BrotliCompression.StringStoreLengthsMessage", "Mirage.Serialization.BrotliCompression.StringStoreStringsMessage",
    "NuclearOption.SceneLoading.LoadMapMessage", "NuclearOption.Networking.LoadWaitingSceneMessage",
    "NuclearOption.Networking.HostEndedMessage", "NuclearOption.Networking.ServerLoadingProgressMessage",
    "NuclearOption.NetworkTransforms.SendTransformBatcher+TransformMessage",
    "NuclearOption.Networking.Authentication.NetworkAuthenticatorNuclearOption+AuthMessage",
    "NetworkMission+SyncMission", "NetworkMission+SyncMissionStart", "NetworkMission+SyncMissionHeader",
    "NetworkMission+SyncMissionPart", "NetworkMission+SyncMissionFooter",
]


def stable_id(name):
    value = 23
    for ch in name:
        value = (value * 31 + ord(ch)) & 0xFFFFFFFF
    return value & 0xFFFF


NAMES = {stable_id(n): n.rsplit(".", 1)[-1] for n in KNOWN}


def read(path):
    with open(path, "rb") as f:
        data = f.read()
    if data[:7] != MAGIC:
        raise SystemExit(f"{path}: not a Nuclear Replay demo")
    (header_len,) = struct.unpack_from("<i", data, 8)
    header = json.loads(data[12:12 + header_len])
    pos, records, truncated = 12 + header_len, [], False
    while pos < len(data):
        if pos + 22 > len(data):
            truncated = True
            break
        kind, t, net, channel, length = struct.unpack_from("<Bddbi", data, pos)
        if pos + 22 + length > len(data):
            truncated = True
            break
        records.append((kind, t, net, channel, data[pos + 22:pos + 22 + length]))
        pos += 22 + length
    return header, records, truncated


def unit(ref):
    if not isinstance(ref, dict):
        return "-"
    text = f"{ref.get('name') or '?'}#{ref.get('id')}"
    if ref.get("player"):
        text += f" [{ref['player']}]"
    if ref.get("faction"):
        text += f" ({ref['faction']})"
    return text


def describe(event):
    kind = event.get("event")
    if kind == "mission":
        return f"mission {event.get('name')!r} map {event.get('map')!r}"
    if kind == "player":
        return f"player {event.get('name')!r} {event.get('faction')} unit {unit(event.get('unit'))}"
    if kind == "launch":
        return f"launch {event.get('weapon')} #{event.get('missile')} by {unit(event.get('shooter'))} at {unit(event.get('target'))}"
    if kind == "kill":
        return f"kill {unit(event.get('victim'))} by {unit(event.get('killer'))} with {event.get('weapon') or '?'}"
    return json.dumps(event)[:300]


def main():
    args = sys.argv[1:]
    events_only = "--events" in args
    args = [a for a in args if a != "--events"]
    for path in args:
        header, records, truncated = read(path)
        if events_only:
            for kind, t, net, _, payload in records:
                if kind == 2:
                    event = json.loads(payload)
                    print(json.dumps({"t": round(t - records[0][1], 3), **event}))
            continue
        print(path)
        print("  header:", json.dumps(header, indent=None))
        messages = [r for r in records if r[0] == 1]
        if messages:
            span = messages[-1][1] - messages[0][1]
            total = sum(len(r[4]) for r in messages)
            print(f"  {len(messages)} messages, {total} bytes over {span:.1f}s ({total / max(span, 1e-9) / 1024:.1f} KiB/s){' TRUNCATED tail' if truncated else ''}")
        counts, sizes = Counter(), Counter()
        for _, _, _, channel, payload in messages:
            mid = payload[0] | payload[1] << 8 if len(payload) >= 2 else 0
            key = (NAMES.get(mid, f"id {mid}"), ("reliable", "unreliable", "notify")[channel])
            counts[key] += 1
            sizes[key] += len(payload)
        for key, count in counts.most_common():
            print(f"    {key[0]:32s} {key[1]:10s} {count:8d} msgs {sizes[key]:10d} bytes")
        events = Counter()
        for kind, t, net, _, payload in records:
            if kind == 2:
                try:
                    event = json.loads(payload)
                except ValueError:
                    event = {"event": "unparsed", "text": payload.decode("utf-8", "replace")}
                events[event.get("event")] += 1
                print(f"  t={t - records[0][1]:8.2f}s net={net:8.2f}  {describe(event)}")
        if events:
            print("  events:", ", ".join(f"{k} {v}" for k, v in events.most_common()))


if __name__ == "__main__":
    main()
