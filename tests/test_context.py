"""Synthetic tests. These fixtures do not represent gameplay captures."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("context", Path(__file__).parents[1] / "tools/context.py")
context = importlib.util.module_from_spec(spec)
spec.loader.exec_module(context)


class TimingTests(unittest.TestCase):
    video = {"filename": "synthetic.mp4", "sha256": "synthetic", "duration_s": 120, "fps": 30}

    def anchor(self, time, video, event):
        return {"real_time_s": time, "video_time_s": video, "event_id": event, "game_frame_interval_s": 1 / 60}

    def segment(self, a=10, b=40, offset=-5, label="first"):
        return context.make_segment(self.video, [self.anchor(a, a + offset, label + "a"),
                                                self.anchor(b, b + offset, label + "b")], label, 0.01)

    def test_game_pause_uses_real_time(self):
        segment = self.segment()
        paused = {"real_time_s": 20, "game_time_s": 12, "time_scale": 0}
        resumed = {"real_time_s": 25, "game_time_s": 12, "time_scale": 1}
        self.assertEqual(context.map_record(paused, [segment])["video_time_s"], 15)
        self.assertEqual(context.map_record(resumed, [segment])["video_time_s"], 20)

    def test_recording_pause_requires_separate_segments(self):
        first = self.segment(10, 20, -5, "first")
        second = self.segment(30, 40, -15, "second")
        self.assertIsNone(context.map_record({"real_time_s": 25}, [first, second])["video_time_s"])
        self.assertEqual(context.map_record({"real_time_s": 35}, [first, second])["video_time_s"], 20)
        with self.assertRaisesRegex(ValueError, "offsets disagree"):
            context.make_segment(self.video, [self.anchor(10, 5, "a"), self.anchor(40, 25, "b")], "bad", 0.01)

    def test_stop_restart_and_unknown_outside_anchors(self):
        segments = [self.segment(10, 20, -10, "file1"), self.segment(50, 70, -50, "file2")]
        segments[1]["video"] = dict(self.video, filename="second.mp4", sha256="second")
        self.assertIsNone(context.map_record({"real_time_s": 30}, segments)["video_time_s"])
        self.assertEqual(context.map_record({"real_time_s": 60}, segments)["video_filename"], "second.mp4")
        self.assertIsNone(context.map_record({"real_time_s": 71}, segments)["video_time_s"])
        self.assertIsNone(context.map_record({"real_time_s": 10}, [])["video_time_s"])

    def test_residual_measured_without_fitting_away_pauses(self):
        segment = context.make_segment(self.video, [self.anchor(10, 5, "a"), self.anchor(30, 25.02, "b"),
                                                   self.anchor(40, 35, "c")], "first", 0.01)
        self.assertAlmostEqual(segment["max_anchor_residual_s"], 0.0133333333333)
        self.assertGreater(segment["uncertainty_s"], 1 / 30)
        self.assertIsNone(segment["presentation_latency_s"])

    def test_invalid_anchors(self):
        for anchors in ([], [self.anchor(1, 1, "a")], [self.anchor(1, 1, "a"), self.anchor(2, 2, "a")],
                        [self.anchor(1, 1, "a"), self.anchor(2, -1, "b")]):
            with self.assertRaises(ValueError):
                context.make_segment(self.video, anchors, "bad", 0.01)


class BundleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.bundle = Path(self.temp.name)
        self.session = {"schema_version": "1.0", "session_id": "synthetic", "synthetic": True,
                        "hooks": {"launch": True}, "recording_alignment": {"segments": []}}
        self.events = []
        self.entity1 = {"entity_id": "synthetic:m1:e1", "type": "Darkreach", "position_m": [33000, 0, 0]}
        self.entity2 = {"entity_id": "synthetic:m1:e2", "type": "Other target", "position_m": [0, 0, 20000]}
        self.player = {"entity_id": "synthetic:m1:e3", "type": "Revoker", "position_m": [0, 0, 0]}

    def tearDown(self):
        self.temp.cleanup()

    def event(self, kind, time, data, mission="synthetic:m1"):
        event = {"schema_version": "1.0", "session_id": "synthetic", "event_id": f"synthetic:v{len(self.events)+1}",
                 "mission_id": mission, "kind": kind, "real_time_s": time, "game_time_s": time,
                 "video_time_s": None, "data": data}
        self.events.append(event)
        return event["event_id"]

    def launch(self, projectile, target, time):
        return self.event("weapon_launch", time, {"player": self.player, "assigned_target": target,
                         "selected_targets": [self.entity2], "projectile_id": projectile,
                         "weapon": {"name": "Scythe", "type": "missile"},
                         "slant_range_m": 33000 if target == self.entity1 else 20000})

    def save(self):
        context.save_json(self.bundle / "session.json", self.session)
        (self.bundle / "events.jsonl").write_text("".join(json.dumps(r) + "\n" for r in self.events))
        (self.bundle / "states.jsonl").write_text("")
        context.save_json(self.bundle / "writer-status.json", {"clean_shutdown": True, "dropped_events": 0, "dropped_states": 0})

    def test_multiple_missiles_selection_and_unknown_outcomes(self):
        self.event("target_selected", 11, {"target": self.entity1})
        first = self.launch("synthetic:m1:e10", self.entity1, 15)
        self.event("target_deselected", 17, {"target": self.entity1})
        self.event("target_selected", 18, {"target": self.entity2})
        second = self.launch("synthetic:m1:e11", self.entity2, 20)
        self.event("missile_target_changed", 22, {"projectile_id": "synthetic:m1:e10", "launch_event_id": first, "target": self.entity2})
        self.event("missile_detonated", 25, {"projectile_id": "synthetic:m1:e11", "launch_event_id": second})
        self.event("entity_removed", 30, {"entity_id": "synthetic:m1:e10", "projectile_id": "synthetic:m1:e10", "launch_event_id": first, "outcome": "unknown"})
        self.event("unit_destruction_reported", 31, {"victim": self.entity1, "kill_attribution": "unknown"})
        self.save()
        result = context.validate_bundle(self.bundle)
        self.assertEqual(result["errors"], [])
        self.assertEqual(result["launch_count"], 2)
        self.assertIn("Darkreach", context.describe(self.events[1]))
        self.assertNotIn("Other target", context.describe(self.events[1]))
        self.assertIn("Outcome unknown", context.describe(self.events[-2]))
        self.assertIn("Kill attribution unknown", context.describe(self.events[-1]))

    def test_respawn_and_mission_boundary_reference_validation(self):
        first = self.launch("synthetic:m1:e10", self.entity1, 15)
        self.event("player_respawn_observed", 20, {"player": dict(self.player, entity_id="synthetic:m1:e20")})
        self.event("missile_detonated", 25, {"projectile_id": "synthetic:m2:e10", "launch_event_id": first}, "synthetic:m2")
        self.save()
        self.assertTrue(any("crosses mission" in e for e in context.validate_bundle(self.bundle)["errors"]))

    def test_range_corruption_and_strict_acceptance(self):
        self.launch("synthetic:m1:e10", self.entity1, 15)
        self.events[0]["data"]["slant_range_m"] = 20000
        self.save()
        result = context.validate_bundle(self.bundle, strict=True)
        self.assertTrue(any("range mismatch" in e for e in result["errors"]))
        self.assertIn("Launch video alignment incomplete", result["errors"])

    def test_query_includes_supporting_launch_and_summary_ids(self):
        first = self.launch("synthetic:m1:e10", self.entity1, 15)
        self.event("missile_detonated", 30, {"projectile_id": "synthetic:m1:e10", "launch_event_id": first})
        self.session["recording_alignment"]["segments"] = [TimingTests().segment()]
        self.save()
        args = SimpleNamespace(bundle=self.bundle, video=Path("synthetic.mp4"), start=24, end=26, states=False)
        output = io.StringIO()
        with patch.object(context, "video_info", return_value=TimingTests.video), contextlib.redirect_stdout(output):
            context.command_query(args)
        lines = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual([r["channel"] for r in lines], ["events", "supporting_event"])
        self.assertEqual(lines[1]["record"]["event_id"], first)
        with patch.object(context, "video_info", return_value=TimingTests.video), contextlib.redirect_stdout(io.StringIO()):
            context.command_export(SimpleNamespace(bundle=self.bundle, video=args.video, output=None))
        timeline = (self.bundle / "timeline.md").read_text()
        self.assertIn(first, timeline)
        self.assertIn("00:10.000", timeline)

    def observation(self, projectile="synthetic:m1:e10", time=15):
        self.session["schema_version"] = "1.1"
        return self.event("projectile_observed", time, {"projectile_id": projectile,
            "time_basis": "client_local_observation", "launch_real_time_s": None,
            "assigned_target": None, "slant_range_m": None, "reported_start_position_m": [1, 2, 3],
            "observed_game_target_id": 100})

    def test_client_observation_is_not_launch_proof(self):
        observation = self.observation()
        self.event("missile_target_changed", 16, {"projectile_id": "synthetic:m1:e10",
            "projectile_observation_event_id": observation, "new_game_target_id": 200, "launch_event_id": None})
        self.event("missile_detonated", 25, {"projectile_id": "synthetic:m1:e10",
            "projectile_observation_event_id": observation, "launch_event_id": None})
        self.save()
        result = context.validate_bundle(self.bundle)
        self.assertEqual(result["errors"], [])
        self.assertEqual(result["launch_count"], 0)
        self.assertEqual(result["projectile_observation_count"], 1)
        self.assertIn("No engagement launch recorded", result["warnings"])
        self.assertIn("launch time, original assignment and launch range unknown", context.describe(self.events[0]))

    def test_receipt_cannot_claim_launch_context(self):
        self.observation()
        self.events[0]["data"]["slant_range_m"] = 500
        self.save()
        self.assertTrue(any("claims launch context" in x for x in context.validate_bundle(self.bundle)["errors"]))

    def test_observation_link_rejects_other_projectile_and_mission(self):
        observation = self.observation()
        self.event("missile_detonated", 25, {"projectile_id": "synthetic:m2:e10",
            "projectile_observation_event_id": observation}, mission="synthetic:m2")
        self.save()
        self.assertTrue(any("reference mismatch" in x for x in context.validate_bundle(self.bundle)["errors"]))

    def test_request_cannot_invent_projectile_link(self):
        self.event("missile_launch_request_observed", 12, {"projectile_id": "synthetic:m1:e10"})
        self.save()
        self.assertTrue(any("invents projectile" in x for x in context.validate_bundle(self.bundle)["errors"]))

    def test_query_includes_client_observation_evidence(self):
        observation = self.observation()
        self.event("missile_detonated", 30, {"projectile_id": "synthetic:m1:e10",
            "projectile_observation_event_id": observation, "launch_event_id": None})
        self.session["recording_alignment"]["segments"] = [TimingTests().segment()]
        self.save()
        args = SimpleNamespace(bundle=self.bundle, video=Path("synthetic.mp4"), start=24, end=26, states=False)
        output = io.StringIO()
        with patch.object(context, "video_info", return_value=TimingTests.video), contextlib.redirect_stdout(output):
            context.command_query(args)
        lines = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(lines[-1]["channel"], "supporting_event")
        self.assertEqual(lines[-1]["record"]["event_id"], observation)


if __name__ == "__main__":
    unittest.main()
