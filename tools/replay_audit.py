#!/usr/bin/env python3
"""Read-only pose-coverage audit. This is not a renderer or a replay success test."""
import argparse
import json
import math
from pathlib import Path

from context import read_session, records


def valid_vector(value, length):
    return (isinstance(value, list) and len(value) == length
            and all(isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v) for v in value))


def audit(bundle):
    bundle = Path(bundle)
    session = read_session(bundle)
    tracks = {}
    omitted = failures = state_count = 0
    world_batches = 0
    invalid = []
    min_time = max_time = None
    max_read_ms = 0
    last_full = {}
    for record in records(bundle / "states.jsonl"):
        state_count += 1
        data = record.get("data", {})
        if record["kind"] == "world_motion":
            world_batches += 1
            entities = data.get("entities", [])
            # Since 0.4.1 an unchanged unit is listed by ID; it repeats its last full snapshot at the batch sample time.
            for identity in data.get("unchanged_entity_ids", []):
                if identity not in last_full:
                    invalid.append("Unchanged entity has no earlier full snapshot: " + identity)
                    continue
                entities.append(dict(last_full[identity], sample_real_time_s=data.get("unchanged_sample_real_time_s")))
            omitted += data.get("omitted_due_to_limit", 0)
            failures += data.get("failed_entity_samples", 0)
            max_read_ms = max(max_read_ms, data.get("capture_read_ms", 0))
        elif record["kind"] == "context" and session["schema_version"] == "1.0":
            entities = [data.get("player")] + data.get("selected_targets", [])
        else:
            continue
        for entity in entities:
            if not entity:
                continue
            identity = entity.get("entity_id", "")
            if not identity.startswith(record["mission_id"] + ":e"):
                invalid.append("Entity identity crosses mission: " + identity)
                continue
            timestamp = entity.get("sample_real_time_s", record["real_time_s"])
            if not isinstance(timestamp, (int, float)) or not math.isfinite(timestamp):
                invalid.append("Invalid sample time: " + identity)
                continue
            track = tracks.setdefault(identity, {"samples": 0, "complete_pose_samples": 0,
                "missing_orientation_samples": 0, "missing_velocity_samples": 0,
                "first_real_time_s": timestamp, "last_real_time_s": timestamp,
                "maximum_gap_s": 0, "motion_sources": set(), "definition_key": entity.get("definition_key")})
            if track["samples"] and timestamp < track["last_real_time_s"]:
                invalid.append("Nonmonotonic entity samples: " + identity)
            track["maximum_gap_s"] = max(track["maximum_gap_s"], timestamp - track["last_real_time_s"])
            track["last_real_time_s"] = timestamp
            track["samples"] += 1
            last_full[identity] = entity
            quaternion = entity.get("rotation_xyzw")
            orientation_valid = valid_vector(quaternion, 4) and abs(sum(x*x for x in quaternion) - 1) < 0.01
            track["missing_orientation_samples"] += not orientation_valid
            track["missing_velocity_samples"] += not valid_vector(entity.get("velocity_mps"), 3)
            track["complete_pose_samples"] += orientation_valid and valid_vector(entity.get("position_m"), 3)
            track["motion_sources"].add(entity.get("motion_source", "unspecified_legacy_local_sample"))
            min_time = timestamp if min_time is None else min(min_time, timestamp)
            max_time = timestamp if max_time is None else max(max_time, timestamp)
    for track in tracks.values():
        track["motion_sources"] = sorted(track["motion_sources"])
    kinds = {}
    for record in records(bundle / "events.jsonl"):
        kinds[record["kind"]] = kinds.get(record["kind"], 0) + 1
    duration = (max_time - min_time) if min_time is not None else 0
    byte_count = sum((bundle / name).stat().st_size for name in ("events.jsonl", "states.jsonl"))
    return {"synthetic": session.get("synthetic", False), "session_id": session["session_id"],
        "state_records": state_count, "motion_batches": world_batches, "tracks": tracks,
        "event_counts": kinds, "sampled_duration_s": duration,
        "observed_json_bytes": byte_count, "projected_json_gb_per_hour_at_observed_rate": byte_count / duration * 3600 / 1e9 if duration else None,
        "omitted_entity_samples": omitted, "failed_entity_samples": failures,
        "maximum_capture_read_ms": max_read_ms, "errors": invalid,
        "tracks_with_at_least_two_complete_poses": sum(t["complete_pose_samples"] >= 2 for t in tracks.values()),
        "actual_game_visuals_proven": False,
        "note": "Pose coverage only. This audit does not prove world completeness, hook execution, faithful effects, clock alignment, or working in-engine playback."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    args = parser.parse_args()
    print(json.dumps(audit(args.bundle), indent=2, allow_nan=False))
