"""Synthetic pose fixtures only; no game playback is performed."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).parents[1] / "tools"))
import replay_audit


class ReplayAuditTests(unittest.TestCase):
    def audit(self, orientations=True, wrong_mission=False):
        with tempfile.TemporaryDirectory() as folder:
            bundle = Path(folder)
            (bundle / "session.json").write_text(json.dumps({"schema_version": "1.1", "session_id": "synthetic", "synthetic": True}))
            (bundle / "events.jsonl").write_text("")
            states = []
            for time in (1, 1.05, 4):
                entity = {"entity_id": "synthetic:m2:e1" if wrong_mission else "synthetic:m1:e1",
                    "position_m": [1000*time, 20, 10], "velocity_mps": [1000, 0, 0],
                    "motion_source": "client_representation_interpolated_or_extrapolated"}
                if orientations:
                    entity["rotation_xyzw"] = [0, 0, 0, 1]
                states.append({"kind": "world_motion", "mission_id": "synthetic:m1", "real_time_s": time,
                    "data": {"entities": [entity], "omitted_due_to_limit": 2, "capture_read_ms": 1.25}})
            (bundle / "states.jsonl").write_text("".join(json.dumps(s) + "\n" for s in states))
            return replay_audit.audit(bundle)

    def test_reports_gaps_and_limits_without_visual_claim(self):
        report = self.audit()
        self.assertTrue(report["synthetic"])
        self.assertEqual(report["omitted_entity_samples"], 6)
        self.assertEqual(report["tracks_with_at_least_two_complete_poses"], 1)
        self.assertAlmostEqual(report["tracks"]["synthetic:m1:e1"]["maximum_gap_s"], 2.95)
        self.assertFalse(report["actual_game_visuals_proven"])

    def test_missing_orientation_is_not_invented(self):
        self.assertEqual(self.audit(False)["tracks_with_at_least_two_complete_poses"], 0)

    def test_unchanged_ids_repeat_last_full_snapshot(self):
        with tempfile.TemporaryDirectory() as folder:
            bundle = Path(folder)
            (bundle / "session.json").write_text(json.dumps({"schema_version": "1.1", "session_id": "synthetic", "synthetic": True}))
            (bundle / "events.jsonl").write_text("")
            entity = {"entity_id": "synthetic:m1:e1", "position_m": [1, 2, 3], "rotation_xyzw": [0, 0, 0, 1],
                "velocity_mps": None, "sample_real_time_s": 1, "motion_source": "client_representation_interpolated_or_extrapolated"}
            states = [{"kind": "world_motion", "mission_id": "synthetic:m1", "real_time_s": 1, "data": {"entities": [entity]}}]
            for time in (1.1, 1.2):
                states.append({"kind": "world_motion", "mission_id": "synthetic:m1", "real_time_s": time,
                    "data": {"entities": [], "unchanged_entity_ids": ["synthetic:m1:e1", "synthetic:m1:e9"], "unchanged_sample_real_time_s": time}})
            (bundle / "states.jsonl").write_text("".join(json.dumps(s) + "\n" for s in states))
            report = replay_audit.audit(bundle)
        track = report["tracks"]["synthetic:m1:e1"]
        self.assertEqual(track["samples"], 3)
        self.assertEqual(track["complete_pose_samples"], 3)
        self.assertAlmostEqual(track["maximum_gap_s"], 0.1)
        self.assertEqual(len(report["errors"]), 2)

    def test_mission_aliasing_rejected(self):
        report = self.audit(wrong_mission=True)
        self.assertEqual(report["tracks"], {})
        self.assertEqual(len(report["errors"]), 3)
