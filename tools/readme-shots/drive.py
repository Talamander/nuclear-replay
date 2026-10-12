#!/usr/bin/env python3
"""Drive the isolated test copy of Nuclear Option for the README screenshots. Test-only; never shipped.

The game runs headless through scripts/launch-test-copy.sh (no window on the desktop). It is driven through the
replay plugin's existing test hook (-ncViewerCommands: one command per line, see RunCommand in
replay/src/ReplayPlugin.Viewer.cs) and, for the GIF, the test-only ReadmeBurst plugin (-readmeBurst).

  drive.py start [game args...]      launch; prints the gamescope pid (kill only that pid to stop)
  drive.py cmd "<command>" ...       append viewer commands (e.g. "menu-button", "seek 30", "preset 1")
  drive.py shot <name>               ScreenCapture.CaptureScreenshot into $OUT/<name>.png, waits for the file
  drive.py wait <python expr> [s]    wait until the expression is true for the status JSON `s`
  drive.py status                    print the plugin's status JSON
  drive.py burst <name> <sec> <fps> <width>   record raw frames for the GIF into $WORK/burst-<name>
  drive.py stop                      kill the gamescope pid this tool started

Environment: README_SHOTS_WORK (default /mnt/games/nuclear-replay-branding/readme/_work),
README_SHOTS_OUT (default /mnt/games/nuclear-replay-branding/readme/raw).
"""
import json
import os
import signal
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
WORK = os.environ.get("README_SHOTS_WORK", "/mnt/games/nuclear-replay-branding/readme/_work")
OUT = os.environ.get("README_SHOTS_OUT", "/mnt/games/nuclear-replay-branding/readme/raw")
CMDS = os.path.join(WORK, "viewer-commands.txt")
BURST = os.path.join(WORK, "burst-commands.txt")
PIDFILE = os.path.join(WORK, "gamescope.pid")


def status():
    try:
        with open(CMDS + ".status") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def append(path, line):
    with open(path, "a") as f:
        f.write(line + "\n")


def wait(expr, timeout=180.0):
    end = time.time() + timeout
    while time.time() < end:
        s = status()
        if s is not None:
            try:
                if eval(expr, {}, {"s": s}):
                    return s
            except Exception:
                pass
        time.sleep(0.25)
    raise SystemExit(f"timeout waiting for {expr}; last status: {json.dumps(status())}")


def main(argv):
    if not argv:
        print(__doc__)
        return 2
    op, args = argv[0], argv[1:]
    os.makedirs(WORK, exist_ok=True)
    os.makedirs(OUT, exist_ok=True)
    if op == "start":
        for p in (CMDS, BURST, CMDS + ".status", BURST + ".burst-status"):
            if os.path.exists(p):
                os.remove(p)
        open(CMDS, "w").close()
        open(BURST, "w").close()
        log = open(os.path.join(WORK, "launch.log"), "a")
        proc = subprocess.Popen(
            [os.path.join(REPO, "scripts", "launch-test-copy.sh"), "-ncViewerCommands", CMDS, "-readmeBurst", BURST, *args],
            stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, start_new_session=True)
        # The wrapper execs gamescope after taking the lock, so this pid becomes the gamescope pid.
        with open(PIDFILE, "w") as f:
            f.write(str(proc.pid))
        print(proc.pid)
        return 0
    if op == "cmd":
        for line in args:
            append(CMDS, line)
        return 0
    if op == "shot":
        name = args[0]
        target = os.path.join(OUT, name + ".png")
        if os.path.exists(target):
            os.remove(target)
        append(CMDS, "shot " + target)
        end = time.time() + 30
        last = -1
        while time.time() < end:
            if os.path.exists(target):
                size = os.path.getsize(target)
                if size > 0 and size == last:
                    print(target, size)
                    return 0
                last = size
            time.sleep(0.4)
        raise SystemExit("no screenshot " + target)
    if op == "wait":
        s = wait(args[0], float(args[1]) if len(args) > 1 else 180.0)
        print(json.dumps({k: s.get(k) for k in ("time", "live", "seeking", "preset", "following", "free_cam", "menu_open", "fps")}))
        return 0
    if op == "status":
        print(json.dumps(status(), indent=1))
        return 0
    if op == "burst":
        name, seconds, fps, width = args
        out = os.path.join(WORK, "burst-" + name)
        os.makedirs(out, exist_ok=True)
        for f in os.listdir(out):
            os.remove(os.path.join(out, f))
        append(BURST, f"burst {out} {seconds} {fps} {width}")
        print(out)
        return 0
    if op == "burst-wait":
        out = os.path.join(WORK, "burst-" + args[0])
        end = time.time() + float(args[1] if len(args) > 1 else 120)
        while time.time() < end:
            if os.path.exists(os.path.join(out, "done.txt")):
                print(open(os.path.join(out, "done.txt")).read())
                return 0
            time.sleep(0.5)
        raise SystemExit("burst not done")
    if op == "stop":
        pid = int(open(PIDFILE).read())
        try:
            with open(f"/proc/{pid}/cmdline", "rb") as f:
                cmdline = f.read().replace(b"\0", b" ").decode(errors="replace")
        except OSError:
            print("not running")
            return 0
        if not cmdline.startswith("gamescope --backend headless"):
            raise SystemExit(f"pid {pid} is not the headless gamescope: {cmdline[:80]}")
        os.kill(pid, signal.SIGTERM)
        for _ in range(60):
            if not os.path.exists(f"/proc/{pid}"):
                print("stopped", pid)
                return 0
            time.sleep(0.5)
        os.kill(pid, signal.SIGKILL)
        print("killed", pid)
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
