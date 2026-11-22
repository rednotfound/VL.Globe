"""ROADMAP 4g: sums up the per-second frame lines of VL.Globe (`Globe perf: ...` in vvvv's log) or of
tools/GLJS-Yardstick.html (`GLJS perf: ...`), so the two can be set side by side.

    python tools/Summarize-Perf.py <log or text file> [--from HH:MM:SS] [--to HH:MM:SS] [--moving]
"""
import re
import sys

LINE = re.compile(r"(?:Globe|GLJS) perf: (\d+) frames, (\d+) fps, mean ([\d.]+) p50 ([\d.]+) p95 ([\d.]+) p99 ([\d.]+) "
                  r"max (\d+) ms, (\d+) over 20 ms,? (moving|still)")
TIME = re.compile(r"T?(\d\d:\d\d:\d\d)")
EXTRA = {
    "uploaded MB": re.compile(r"uploaded \d+ tiles ([\d.]+) MB"),
    "submit mean ms": re.compile(r"submit [\d.]+/([\d.]+) ms"),
    "draws": re.compile(r", (\d+) draws"),
    "Update max ms": re.compile(r"Update max ([\d.]+) ms"),
}


def main(argv):
    path = argv[1]
    start = argv[argv.index("--from") + 1] if "--from" in argv else None
    end = argv[argv.index("--to") + 1] if "--to" in argv else None
    moving_only = "--moving" in argv
    seconds, frames, missed, weighted = 0, 0, 0, 0.0
    p95s, p99s, worst = [], [], 0
    extras = {k: [] for k in EXTRA}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = LINE.search(line)
            if not m:
                continue
            t = TIME.search(line)
            if t and ((start and t.group(1) < start) or (end and t.group(1) > end)):
                continue
            if moving_only and m.group(9) != "moving":
                continue
            n = int(m.group(1))
            seconds += 1
            frames += n
            weighted += float(m.group(3)) * n
            p95s.append(float(m.group(5)))
            p99s.append(float(m.group(6)))
            worst = max(worst, int(m.group(7)))
            missed += int(m.group(8))
            for k, rx in EXTRA.items():
                e = rx.search(line)
                if e:
                    extras[k].append(float(e.group(1)))
    if seconds == 0:
        print("no perf lines")
        return
    p95s.sort()
    mean = weighted / frames
    print(f"{seconds} seconds, {frames} frames: {1000 / mean:.1f} fps (mean frame {mean:.1f} ms); "
          f"{missed} frames over 20 ms = {100 * missed / frames:.1f}%; "
          f"p95 median {p95s[len(p95s) // 2]:.1f} ms, worst p99 {max(p99s):.1f} ms, worst frame {worst} ms")
    for k, v in extras.items():
        if v:
            print(f"  {k}: mean {sum(v) / len(v):.1f}, max {max(v):.1f}")


if __name__ == "__main__":
    main(sys.argv)
