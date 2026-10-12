#!/usr/bin/env python3
"""Local Nuclear Replay bundle alignment, timeline export, and range queries."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import subprocess
import sys


def records(path):
    with Path(path).open(encoding="utf-8-sig") as stream:
        for number, line in enumerate(stream, 1):
            if not line.strip():
                continue
            try:
                yield json.loads(line)
            except json.JSONDecodeError as error:
                raise ValueError(f"Invalid JSON at {path}:{number}; close the game and check writer-status.json") from error


def read_session(bundle):
    result = json.loads((Path(bundle) / "session.json").read_text(encoding="utf-8-sig"))
    if result.get("schema_version") not in ("1.0", "1.1"):
        raise ValueError("Unsupported schema version")
    return result


def save_json(path, value):
    path = Path(path)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    temporary.replace(path)


def video_info(path):
    path = Path(path).resolve(strict=True)
    before = path.stat()
    data = json.loads(subprocess.check_output([
        "ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
        "stream=avg_frame_rate,start_time:format=duration", "-of", "json", str(path)
    ], text=True))
    if not data.get("streams"):
        raise ValueError("No video stream")
    numerator, denominator = data["streams"][0]["avg_frame_rate"].split("/")
    fps = float(numerator) / float(denominator) if float(denominator) else None
    if not fps or fps <= 0:
        raise ValueError("Unknown frame rate; video requires usable presentation timestamps")
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    after = path.stat()
    if (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) != (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns):
        raise ValueError("Video is changing; stop recording before final alignment or export")
    return {"path": str(path), "filename": path.name, "sha256": digest.hexdigest(),
            "duration_s": float(data["format"]["duration"]), "fps": fps,
            "time_reference": "seconds from recording start, using presentation timestamps"}


def make_segment(video, anchors, label, uncertainty):
    if not math.isfinite(uncertainty) or uncertainty < 0:
        raise ValueError("Uncertainty must be finite and nonnegative")
    anchors = sorted(anchors, key=lambda item: item["real_time_s"])
    if len(anchors) < 2:
        raise ValueError("Use at least two visible marker anchors per uninterrupted recording segment")
    if len({a["event_id"] for a in anchors}) != len(anchors):
        raise ValueError("Each anchor must reference a different marker event")
    for first, second in zip(anchors, anchors[1:]):
        if second["real_time_s"] <= first["real_time_s"] or second["video_time_s"] <= first["video_time_s"]:
            raise ValueError("Anchor times must increase")
    for anchor in anchors:
        if not math.isfinite(anchor["video_time_s"]) or not 0 <= anchor["video_time_s"] <= video["duration_s"]:
            raise ValueError("Anchor is outside the video")
    offsets = [a["video_time_s"] - a["real_time_s"] for a in anchors]
    offset = sum(offsets) / len(offsets)
    residual = max(abs(value - offset) for value in offsets)
    frame_error = 1 / video["fps"]
    game_frame_error = max(a.get("game_frame_interval_s", 0) for a in anchors)
    # Real time and video time advance at the same rate. A pause is never absorbed into a fitted slope.
    drift_allowance = (anchors[-1]["real_time_s"] - anchors[0]["real_time_s"]) * 0.001
    if max(offsets) - min(offsets) > 2 * uncertainty + 2 * frame_error + game_frame_error + drift_allowance:
        raise ValueError("Anchor offsets disagree: recording pause/cut or wrong marker. Add separate segments.")
    return {"segment_id": label, "video": video, "anchors": anchors,
            "real_start_s": anchors[0]["real_time_s"], "real_end_s": anchors[-1]["real_time_s"],
            "offset_s": offset, "mapping": "video_time = real_time + offset; no extrapolation beyond anchors",
            "uncertainty_s": uncertainty + frame_error + game_frame_error + residual,
            "max_anchor_residual_s": residual, "presentation_latency_s": None,
            "verification": "First visible marker frames reviewed by operator. Capture presentation latency is not measured.",
            "pause_mapping": "Only this uninterrupted active interval is mapped; gaps remain unknown."}


def map_record(record, segments):
    result = dict(record)
    result.update(video_time_s=None, video_filename=None, segment_id=None, timing_uncertainty_s=None)
    time = record["real_time_s"]
    for segment in segments:
        if segment["real_start_s"] <= time <= segment["real_end_s"]:
            result.update(video_time_s=time + segment["offset_s"],
                          video_filename=segment["video"]["filename"], segment_id=segment["segment_id"],
                          timing_uncertainty_s=segment["uncertainty_s"])
            break
    return result


def filtered_segments(session, video=None):
    segments = session.get("recording_alignment", {}).get("segments", [])
    if video is not None:
        info = video_info(video)
        segments = [s for s in segments if s["video"]["sha256"] == info["sha256"]]
    return segments


def format_time(value):
    if value is None:
        return "video time unknown"
    minutes, seconds = divmod(value, 60)
    return f"{int(minutes):02d}:{seconds:06.3f}"


def describe(record):
    data = record.get("data", {})
    kind = record["kind"]
    def entity(value):
        return (value or {}).get("type") or (value or {}).get("name") or "unknown entity"
    if kind == "weapon_launch":
        weapon = data.get("weapon") or {}
        target = data.get("assigned_target")
        distance = data.get("slant_range_m")
        range_text = f"{distance / 1000:.3f} km slant range" if distance is not None else "range unknown"
        return (f"{entity(data.get('player'))} launches {weapon.get('name') or 'unknown weapon'} "
                f"({weapon.get('type', 'unknown type')}); spawn assigns {entity(target)}; "
                f"{range_text}; projectile {data.get('projectile_id')}. Outcome remains unknown until supporting events.")
    if kind == "projectile_observed":
        return (f"Projectile {data.get('projectile_id')} first observed locally; launch time, original assignment and launch range unknown. "
                "Replicated start fields do not supply a launch clock.")
    if kind == "missile_launch_request_observed":
        return f"Missile request/animation observed ({data.get('time_basis')}); actual launch and projectile correlation unknown."
    if kind == "entity_observed":
        return f"Entity {entity(data.get('entity'))} observed locally; actual spawn time unknown."
    if kind == "player_loadout_observed":
        return "Player loadout/ammunition sampled; ammunition changes do not establish launches."
    if kind == "mission_objectives_observed":
        return "Local mission objective state sampled; exact transition time and player awareness unknown."
    if kind in ("target_selected", "target_deselected"):
        return f"{kind.replace('_', ' ')}: {entity(data.get('target'))}; player selection only."
    if kind == "weapon_selected":
        return f"Weapon selection: {(data.get('weapon') or {}).get('name') or 'none'}."
    if kind == "missile_detonated":
        return f"Projectile {data.get('projectile_id')} detonation observed ({data.get('time_basis', 'local observation')}). Target hit unknown."
    if kind == "missile_collision":
        return f"Projectile {data.get('projectile_id')} contacts damageable on {entity(data.get('contact_unit'))}. Applied damage unknown."
    if kind == "missile_target_changed":
        return f"Projectile {data.get('projectile_id')} changes simulation target to {entity(data.get('target'))}."
    if kind == "damage_credit_observed":
        source = f"projectile {data['projectile_id']}" if data.get("projectile_id") else "individual projectile unknown"
        return f"Damage credit {data.get('damage_amount')} on {entity(data.get('victim'))}; {source}."
    if kind == "part_damage_observed":
        source = f"Projectile {data['projectile_id']} linked by explicit damage scope." if data.get("projectile_id") else "Projectile unknown."
        return f"Damage processing observed on {entity(data.get('victim'))}, part {data.get('part_index')}. {source}"
    if kind == "unit_destruction_reported":
        return f"Simulation reports destruction of {entity(data.get('victim'))}. Kill attribution unknown."
    if kind == "player_aircraft_changed":
        return f"Player aircraft changes to {entity(data.get('player'))}; entity {(data.get('player') or {}).get('entity_id')} ."
    if kind == "player_respawn_observed":
        return f"Player enters {entity(data.get('player'))} after prior aircraft destruction; new aircraft identity."
    if kind == "sync_marker":
        return f"Visible marker render submitted: {data['marker']}."
    if kind == "entity_removed":
        return f"Entity {data.get('entity_id')} removed. Outcome unknown."
    if kind == "mission_changed":
        return f"Mission changes to {data.get('name')}; new mission identity {record['mission_id']}."
    if kind == "unit_disabled":
        return f"Unit {entity(data.get('unit'))} disabled; kill attribution unknown."
    return kind.replace("_", " ") + "."


def command_markers(args):
    for record in records(args.bundle / "events.jsonl"):
        if record["kind"] == "sync_marker":
            print(json.dumps(record, allow_nan=False))


def command_align(args):
    if not args.verified:
        raise ValueError("Review first visible marker frames in actual recording, then pass --verified")
    session = read_session(args.bundle)
    marker_events = {r["data"]["marker"]: r for r in records(args.bundle / "events.jsonl") if r["kind"] == "sync_marker"}
    anchors = []
    for binding in args.anchor:
        marker, timestamp = binding.rsplit("=", 1)
        if marker not in marker_events:
            raise ValueError(f"Unknown rendered marker {marker}")
        event = marker_events[marker]
        anchors.append({"marker": marker, "event_id": event["event_id"], "real_time_s": event["real_time_s"],
                        "video_time_s": float(timestamp), "game_frame_interval_s": event["data"].get("game_frame_interval_s", 0)})
    segment = make_segment(video_info(args.video), anchors, args.segment, args.uncertainty)
    alignment = session.setdefault("recording_alignment", {"segments": []})
    existing = [s for s in alignment["segments"] if s["segment_id"] != args.segment]
    for other in existing:
        if max(other["real_start_s"], segment["real_start_s"]) <= min(other["real_end_s"], segment["real_end_s"]):
            raise ValueError("Telemetry intervals overlap; give each active recording interval distinct anchors")
    alignment.update(status="verified_marker_anchors", segments=sorted(existing + [segment], key=lambda s: s["real_start_s"]))
    alignment.pop("pending_review", None)
    save_json(args.bundle / "session.json", session)
    print(json.dumps(segment, indent=2, allow_nan=False))


def command_export(args):
    session = read_session(args.bundle)
    segments = filtered_segments(session, args.video)
    output = args.output or args.bundle / "timeline.md"
    lines = ["# Nuclear Replay timeline", "", f"Session: `{session['session_id']}`.", "",
             "Facts below reference raw event IDs. Target types and outcomes come from simulation data unless stated otherwise.",
             "Selected targets do not establish missile assignment. Destruction does not establish an exclusive player kill.", ""]
    for record in sorted(records(args.bundle / "events.jsonl"), key=lambda r: (r["real_time_s"], r["event_id"])):
        aligned = map_record(record, segments)
        when = format_time(aligned["video_time_s"])
        claim = describe(record).replace("\n", " ").replace("\r", " ")
        reference = f"`{record['event_id']}`"
        if record.get("data", {}).get("launch_event_id"):
            reference += f", launch `{record['data']['launch_event_id']}`"
        if record.get("data", {}).get("projectile_observation_event_id"):
            reference += f", first observation `{record['data']['projectile_observation_event_id']}`"
        filename = f" ({aligned['video_filename']})" if aligned["video_filename"] else ""
        lines.append(f"- **{when}**{filename}, real {record['real_time_s']:.3f}s: {claim} [{reference}]")
    output.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(output)


def command_query(args):
    if not 0 <= args.start <= args.end:
        raise ValueError("Use 0 <= start <= end")
    segments = filtered_segments(read_session(args.bundle), args.video)
    if not segments:
        raise ValueError("No alignment for this video; video time remains unknown")
    support = set()
    for channel in ("events", "states") if args.states else ("events",):
        for record in records(args.bundle / f"{channel}.jsonl"):
            result = map_record(record, segments)
            time = result["video_time_s"]
            if time is not None and args.start <= time <= args.end:
                for key in ("launch_event_id", "projectile_observation_event_id"):
                    if result.get("data", {}).get(key):
                        support.add(result["data"][key])
                print(json.dumps({"channel": channel, "record": result}, allow_nan=False))
    # Include the evidence chain, including first observations without proven launches.
    if support:
        lookup = {r["event_id"]: r for r in records(args.bundle / "events.jsonl")}
        visited = set()
        while support:
            event_id = min(support)
            support.remove(event_id)
            if event_id in visited or event_id not in lookup:
                continue
            visited.add(event_id)
            result = map_record(lookup[event_id], segments)
            for key in ("launch_event_id", "projectile_observation_event_id"):
                if result.get("data", {}).get(key):
                    support.add(result["data"][key])
            if result["video_time_s"] is None or not args.start <= result["video_time_s"] <= args.end:
                print(json.dumps({"channel": "supporting_event", "record": result}, allow_nan=False))


def validate_bundle(bundle, strict=False):
    session = read_session(bundle)
    events = list(records(Path(bundle) / "events.jsonl"))
    errors, warnings = [], []
    ids = [r["event_id"] for r in events]
    if len(ids) != len(set(ids)):
        errors.append("Duplicate event IDs")
    by_id = {r["event_id"]: r for r in events}
    launches = {}
    observations = {}
    for record in events:
        if record["session_id"] != session["session_id"] or not record["mission_id"].startswith(session["session_id"] + ":m"):
            errors.append("Session/mission identity mismatch: " + record["event_id"])
        data = record.get("data", {})
        projectile = data.get("projectile_id")
        if projectile and not projectile.startswith(record["mission_id"] + ":e"):
            errors.append("Projectile identity crosses mission: " + record["event_id"])
        if record["kind"] in ("projectile_observed", "missile_launch_request_observed"):
            for field in ("launch_real_time_s", "launch_game_time_s", "slant_range_m", "horizontal_range_m", "assigned_target", "launch_event_id"):
                if data.get(field) is not None:
                    errors.append("Observation/request claims launch context: " + record["event_id"] + ":" + field)
        if record["kind"] == "missile_launch_request_observed" and projectile:
            errors.append("Request invents projectile correlation: " + record["event_id"])
        if record["kind"] == "projectile_observed":
            if not projectile or projectile in observations:
                errors.append("Missing/duplicate projectile observation: " + record["event_id"])
            observations[projectile] = record
        if record["kind"] == "weapon_launch":
            if record.get("schema_version") == "1.1" and data.get("time_basis") != "host_spawn_invocation":
                errors.append("Launch lacks host spawn evidence: " + record["event_id"])
            projectile = data["projectile_id"]
            if projectile in launches:
                errors.append("Duplicate launch for " + projectile)
            launches[projectile] = record
            if not projectile.startswith(record["mission_id"] + ":e"):
                errors.append("Projectile identity crosses mission: " + projectile)
            target, player = data.get("assigned_target"), data.get("player")
            if target and player:
                measured = math.dist(target["position_m"], player["position_m"])
                if data.get("slant_range_m") is None or not math.isclose(measured, data["slant_range_m"], rel_tol=1e-5, abs_tol=0.1):
                    errors.append("Launch snapshot range mismatch: " + record["event_id"])
            if not data.get("weapon", {}).get("name"):
                errors.append("Launch weapon identity missing: " + record["event_id"])
        reference = data.get("launch_event_id")
        if reference:
            launch = by_id.get(reference)
            if not launch or launch["kind"] != "weapon_launch":
                errors.append("Invalid launch reference: " + record["event_id"])
            elif launch["mission_id"] != record["mission_id"]:
                errors.append("Outcome crosses mission: " + record["event_id"])
            elif data.get("projectile_id") and data["projectile_id"] != launch["data"]["projectile_id"]:
                errors.append("Outcome projectile mismatch: " + record["event_id"])
        reference = data.get("projectile_observation_event_id")
        if reference:
            observation = by_id.get(reference)
            if not observation or observation["kind"] != "projectile_observed":
                errors.append("Invalid projectile observation reference: " + record["event_id"])
            elif observation["mission_id"] != record["mission_id"] or observation["data"].get("projectile_id") != projectile:
                errors.append("Projectile observation reference mismatch: " + record["event_id"])
        if record["kind"] == "hook_error":
            errors.append("Runtime hook error: " + str(data))
    status_path = Path(bundle) / "writer-status.json"
    status = json.loads(status_path.read_text()) if status_path.exists() else {}
    if status.get("fault"):
        errors.append("Writer fault: " + status["fault"])
    if not status.get("clean_shutdown"):
        warnings.append("Bundle not cleanly closed; game may still run")
    if status.get("dropped_events", 0) or status.get("dropped_states", 0):
        warnings.append("Dropped records: " + json.dumps(status))
    missing = [name for name, present in session.get("hooks", {}).items() if not present]
    if missing:
        warnings.append("Missing hooks: " + ", ".join(missing))
    if not launches:
        warnings.append("No engagement launch recorded")
    aligned = [map_record(r, filtered_segments(session)) for r in launches.values()]
    if not aligned or any(r["video_time_s"] is None for r in aligned):
        warnings.append("Launch video alignment incomplete")
    if strict:
        errors.extend(warnings)
    return {"synthetic": session.get("synthetic", False), "launch_count": len(launches), "projectile_observation_count": len(observations),
            "event_count": len(events), "errors": errors, "warnings": warnings, "writer_status": status,
            "acceptance_note": "Structural validation does not prove agreement with footage; review launch HUD/target/range and marker frames."}


def command_validate(args):
    result = validate_bundle(args.bundle, args.strict)
    print(json.dumps(result, indent=2, allow_nan=False))
    return 1 if result["errors"] else 0


def command_frame(args):
    if args.time < 0 or not math.isfinite(args.time):
        raise ValueError("Frame timestamp must be finite and nonnegative")
    # ffmpeg's default accurate input seek decodes/discards from the preceding keyframe.
    subprocess.run(["ffmpeg", "-v", "error", "-ss", str(args.time), "-i", str(args.video),
                    "-frames:v", "1", "-y", str(args.output)], check=True)
    if not args.output.exists():
        raise ValueError("No frame at requested timestamp")
    print(args.output)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    markers = commands.add_parser("markers", help="List rendered markers with raw event evidence")
    markers.add_argument("bundle", type=Path)
    align = commands.add_parser("align", help="Bind first visible marker frames to one active recording segment")
    align.add_argument("video", type=Path); align.add_argument("bundle", type=Path)
    align.add_argument("--anchor", action="append", required=True, metavar="MARKER=SECONDS")
    align.add_argument("--segment", default="recording-1")
    align.add_argument("--uncertainty", type=float, default=0.1, help="Operator timestamp/presentation uncertainty, seconds")
    align.add_argument("--verified", action="store_true", help="Confirm actual video marker frames reviewed")
    export = commands.add_parser("export", help="Write timeline.md with event references")
    export.add_argument("video", type=Path); export.add_argument("bundle", type=Path)
    export.add_argument("--output", type=Path)
    query = commands.add_parser("query", help="Stream records in an aligned video time range")
    query.add_argument("video", type=Path); query.add_argument("bundle", type=Path)
    query.add_argument("--start", type=float, required=True); query.add_argument("--end", type=float, required=True)
    query.add_argument("--states", action="store_true")
    validate = commands.add_parser("validate", help="Check identity, ranges, references, hooks, and loss counts")
    validate.add_argument("bundle", type=Path); validate.add_argument("--strict", action="store_true")
    frame = commands.add_parser("frame", help="Extract an actual video frame for marker/HUD review")
    frame.add_argument("video", type=Path); frame.add_argument("--time", type=float, required=True)
    frame.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        return globals()["command_" + args.command](args) or 0
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        print(f"Error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
