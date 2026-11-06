#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""FPS per segment between SHARPEMU_HOTFLAGS_FILE changes in a run.log.

A segment starts at a group of [HOTFLAG] NAME=old->new lines (consecutive changes with no [PERF][RENDER_MS] line
between them form one boundary) and holds the [PERF][RENDER_MS] windows that follow. The first window after a
boundary straddles the change and is always dropped; the following ones are dropped until the dropped windows sum
at least --skip seconds. FPS = kept frames / kept seconds. A summary pools the segments by flag state.
"""

import argparse
import re
import sys
from collections import OrderedDict

HOTFLAG = re.compile(r"\[HOTFLAG\] ([A-Z0-9_]+)=(-?\d+)->(-?\d+) frame=(\d+)")
WATCHING = re.compile(r"\[HOTFLAG\] watching .* frame=(\d+)((?: [A-Z0-9_]+=-?\d+)*)\s*$")
WINDOW = re.compile(r"\[PERF\]\[RENDER_MS\] window_s=([0-9.]+) frames=(\d+)")


def label(state, names):
    return " ".join(f"{name}={state[name]}" for name in names if name in state) or "initial"


def segments(lines, skip):
    state = OrderedDict()
    changed = []
    result = []
    current = None
    for line in lines:
        watching = WATCHING.search(line)
        if watching:
            for pair in watching.group(2).split():
                name, value = pair.split("=")
                state[name] = value
            continue
        change = HOTFLAG.search(line)
        if change:
            name, _, new, frame = change.groups()
            state[name] = new
            if name not in changed:
                changed.append(name)
            if current is None or current["windows"]:
                current = {"frame": int(frame), "state": dict(state), "windows": []}
                result.append(current)
            else:
                current["state"] = dict(state)
            continue
        window = WINDOW.search(line)
        if window:
            if current is None:
                current = {"frame": 0, "state": dict(state), "windows": [], "initial": True}
                result.append(current)
            current["windows"].append((float(window.group(1)), int(window.group(2))))
    for segment in result:
        segment["label"] = label(segment["state"], changed)
        dropped = 0.0
        kept_s = 0.0
        kept_frames = 0
        kept = 0
        for index, (seconds, frames) in enumerate(segment["windows"]):
            straddles = index == 0 and not segment.get("initial")
            if straddles or dropped < skip:
                dropped += seconds
                continue
            kept += 1
            kept_s += seconds
            kept_frames += frames
        segment.update(kept=kept, kept_s=kept_s, kept_frames=kept_frames, dropped_s=dropped)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("log", help="run.log (stderr of the emulator)")
    parser.add_argument("--skip", type=float, default=5.0, help="seconds dropped at the start of each segment (default 5)")
    parser.add_argument("--min-kept", type=float, default=0.0, help="leave out segments with fewer kept seconds")
    args = parser.parse_args()
    with open(args.log, encoding="utf-8", errors="replace") as handle:
        result = segments(handle, args.skip)
    pooled = OrderedDict()
    print(f"{'seg':>3} {'frame':>8} {'kept_s':>7} {'drop_s':>6} {'fps':>7}  state")
    for number, segment in enumerate(result):
        if segment["kept_s"] <= 0 or segment["kept_s"] < args.min_kept:
            print(f"{number:>3} {segment['frame']:>8} {segment['kept_s']:>7.1f} {segment['dropped_s']:>6.1f} {'-':>7}  {segment['label']}")
            continue
        fps = segment["kept_frames"] / segment["kept_s"]
        print(f"{number:>3} {segment['frame']:>8} {segment['kept_s']:>7.1f} {segment['dropped_s']:>6.1f} {fps:>7.2f}  {segment['label']}")
        entry = pooled.setdefault(segment["label"], {"frames": 0, "seconds": 0.0, "fps": []})
        entry["frames"] += segment["kept_frames"]
        entry["seconds"] += segment["kept_s"]
        entry["fps"].append(fps)
    if pooled:
        print()
        print(f"{'n':>3} {'kept_s':>7} {'pooled':>7} {'mean':>7} {'min':>7} {'max':>7}  state")
        for name, entry in pooled.items():
            values = entry["fps"]
            print(f"{len(values):>3} {entry['seconds']:>7.1f} {entry['frames'] / entry['seconds']:>7.2f} "
                  f"{sum(values) / len(values):>7.2f} {min(values):>7.2f} {max(values):>7.2f}  {name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
