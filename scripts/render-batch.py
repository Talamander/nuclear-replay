#!/usr/bin/env python3
"""One shared replay render for many Shorts.

Usage: scripts/render-batch.py jobs.json [--dry-run]

jobs.json:
  {"demo": "/path/x.ncdemo", "width": 1080, "height": 1920, "fps": 30,
   "jobs": [{"name": "A01", "ranges": "1929:1952,1953:1988", "shots": "/path/shots.json", "out": "/path/clips"},
            {"name": "B02", "ranges": "3663.8:3674.9@60", "shots": "/path/b02-shots.json", "out": "/path/clips"}]}

The game can follow only one shot list per run, so jobs are packed into as few passes as possible:
a job joins a pass when, for each of its ranges, no other job in the pass has a shot between this job's
shot in effect at the range start and the range end (the camera in the range is then exactly what the job's
own list gives). Each pass is ONE render-clips.sh call with the merged shot list and every range (one
boot, one skip through the demo). Clips land in the render cache under the same key a job-alone render
uses, so each job's clips are then copied out by a normal render-clips.sh call that hits the cache, and
an agent that later calls render-clips.sh with its own ranges and shot list gets cache hits too.
"""
import json, os, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))


def parse_ranges(spec, fps):
    out = []
    for item in spec.split(","):
        item = item.strip()
        if not item: continue
        span, _, f = item.partition("@")
        a, b = span.split(":")
        out.append((float(a), float(b), int(f or fps), f"{span}@{int(f or fps)}"))
    return out


def load_shots(path):
    return sorted(json.load(open(path))["shots"], key=lambda s: s.get("t", 0)) if path else []


def canon(shot):
    return json.dumps(shot, sort_keys=True)


def fits(job, members):
    """True when no member's shots fall inside any of job's camera windows and vice versa."""
    def ok(j, others):
        foreign = {canon(s): s for o in others for s in o["shot_list"]}
        own = {canon(s) for s in j["shot_list"]}
        foreign = [s for k, s in foreign.items() if k not in own]
        for a, b, _, _ in j["range_list"]:
            before = [s for s in j["shot_list"] if s.get("t", 0) <= a]
            start = before[-1].get("t", 0) if before else float("-inf")
            if any(start <= s.get("t", 0) <= b for s in foreign):
                return False
        return True
    return ok(job, members) and all(ok(m, [o for o in members if o is not m] + [job]) for m in members)


def main():
    cfg = json.load(open(sys.argv[1])); dry = "--dry-run" in sys.argv
    fps = cfg.get("fps", 30)
    jobs = cfg["jobs"]
    for j in jobs:
        j["shot_list"] = load_shots(j.get("shots"))
        j["range_list"] = parse_ranges(j["ranges"], j.get("fps", fps))
    passes = []
    for j in sorted(jobs, key=lambda j: min(r[0] for r in j["range_list"])):
        for p in passes:
            if fits(j, p):
                p.append(j); break
        else:
            passes.append([j])
    env = dict(os.environ, WIDTH=str(cfg.get("width", 1080)), HEIGHT=str(cfg.get("height", 1920)))
    for n, p in enumerate(passes):
        shots = {canon(s): s for j in p for s in j["shot_list"]}
        merged = sorted(shots.values(), key=lambda s: s.get("t", 0))
        ranges = sorted({r[3] for j in p for r in j["range_list"]}, key=lambda x: float(x.split(":")[0]))
        print(f"pass {n + 1}/{len(passes)}: jobs {', '.join(j['name'] for j in p)}; {len(ranges)} ranges, {len(merged)} shots", flush=True)
        if dry:
            continue
        with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR")) as td:
            sp = f"{td}/shots.json"
            json.dump({"shots": merged}, open(sp, "w"), indent=1)
            # Ranges that two jobs both asked for (same span) are rendered once.
            subprocess.run([f"{HERE}/render-clips.sh", cfg["demo"], ",".join(ranges), f"{td}/clips", sp, str(fps)], env=env)
    # Deliver per job (cache hits; a job whose range failed is rendered on its own here).
    rc = 0
    for j in jobs:
        if dry: continue
        print(f"deliver {j['name']} -> {j['out']}", flush=True)
        args = [f"{HERE}/render-clips.sh", cfg["demo"], j["ranges"], j["out"], j.get("shots") or "", str(j.get("fps", fps))]
        rc |= subprocess.run(args, env=env).returncode
    sys.exit(rc)


if __name__ == "__main__":
    main()
