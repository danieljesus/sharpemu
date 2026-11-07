#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""[local] The DRAW_PLAN / LITE_OBTAIN gate (sharpemu-rediseno-draws.md section 8.3).

Reads a run.log with SHARPEMU_DRAWCOST_PROBE=1, SHARPEMU_LITE_OBTAIN_PROBE=1 and SHARPEMU_PLAN_PROBE=1 and projects
the us per draw of B + A-1 (a plan per draw shape over a light obtain) in closed form, then prints GO / GREY / KILL.

    python plan.py run.log [--base base_run.log] [--windows]

The probes report in the [PERF][DRAWCOST] window: a block is one DRAWCOST header with the kind lines, the
[PERF][LITE_OBTAIN] lines and the [PERF][PLAN] line printed after it. Only in-game blocks count (0 < frames < 60 and
draw items_per_frame > 2000), weighted by frames. --base takes the DRAWCOST steps from a run without the two probes in
the same place (their own cost is inside the steps of the probed run); without it the probed run's steps are used and
the projection is pessimistic by the probes' cost.

Projection per draw (us), with today's steps from DRAWCOST (us per pass x passes per item):
  saving_a1 = obtains/item x fast% x (real_fast_ns - lite_fast_ns)
            + finds/item x find_fast% x (find_ns - find_lite_ns), find_ns = bind_memo_buffers / finds/item (the doc
              divides by memo_buffers_changed, a count of stages, not of lookups), capped at bind_memo_buffers
  fast      = targets block  (1 - eq_targets) x today + eq_targets x 0.25 + run_start_share x today(draw_attachments)
            + draw_pipeline  eq_pipeline x 0.25 + (1 - eq_pipeline) x today
            + dynamic block  (draw_dynamic + exec_fill) eq_dynamic x 0.37 + (1 - eq_dynamic) x today
            + buffer steps   (bind_memo_buffers, res_buffers, draw_buffers) today, then - saving_a1
            + other steps    today x (annex B+A-1 / annex today) for the steps the annex lists, today otherwise
            + gate_ns
  mean      = covered x fast + (1 - covered) x (today + gate_ns)
  mean_cal  = today - 0.6 x (today - mean)
Dispatches: only the A-1 saving on their read obtains and finds (section 6 gives no closed form for the rest).
"""

import re
import sys

# Annex of sharpemu-rediseno-draws.md (cb2, us per draw): today and B + A-1.
ANNEX = {
    "snapshots": (0.08, 0.08), "exec_prelude": (0.89, 0.35), "exec_programs": (0.14, 0.14), "exec_adapt": (0.11, 0.03),
    "rec_hand_over": (0.29, 0.12), "rec_pending": (1.02, 1.02), "prep_begin": (0.93, 0.40), "bind_prelude": (0.42, 0.10),
    "bind_memo_find": (0.56, 0.35), "bind_memo_rent": (0.24, 0.05), "bind_memo_images": (0.33, 0.25),
    "bind_memo_shader_data": (0.33, 0.12), "bind_memo_ranges": (0.07, 0.07), "bind_miss": (0.08, 0.08), "bda": (0.08, 0.08),
    "res_ranges": (0.12, 0.12), "res_upload_end": (0.07, 0.07), "res_images": (0.51, 0.35), "draw_stage_images": (0.68, 0.45),
    "draw_bind_vertex": (0.18, 0.12), "commit_build": (1.01, 0.45), "commit_push": (0.38, 0.30), "commit_tail": (0.10, 0.05),
    "draw_begin_rendering": (0.19, 0.12), "draw_emit": (0.23, 0.20), "draw_tail": (0.10, 0.10), "prep_end": (0.58, 0.25),
    "rec_return": (0.13, 0.05), "exit": (0.07, 0.05),
}
TARGET_STEPS = ("exec_targets", "rec_targets", "draw_attachments")
DYNAMIC_STEPS = ("draw_dynamic", "exec_fill")
BUFFER_STEPS = ("bind_memo_buffers", "res_buffers", "draw_buffers")
TARGETS_EQUAL_US = 0.25
PIPELINE_EQUAL_US = 0.25
DYNAMIC_EQUAL_US = 0.37  # annex B+A-1: draw_dynamic 0.25 + exec_fill 0.12

NUMBER = re.compile(r"^-?[\d.]+$")


def fields(line):
    """key=value tokens; a value a:b,c:d becomes a dict."""
    out = {}
    for token in line.split():
        if "=" not in token:
            continue
        key, value = token.split("=", 1)
        if ":" in value:
            parts = {}
            for item in value.split(","):
                if ":" in item:
                    name, number = item.split(":", 1)
                    parts[name] = float(number) if NUMBER.match(number) else number
            out[key] = parts
        elif NUMBER.match(value):
            out[key] = float(value)
        else:
            out[key] = value
    return out


def drawcost_kind(line):
    head, _, rest = line.partition(" steps=")
    steps_text, _, counts_text = rest.partition(" counts=")
    steps = {}
    for match in re.finditer(r"(\w+)=([\d.]+)/([\d.]+)/([\d.]+)", steps_text):
        steps[match.group(1)] = float(match.group(2)) * float(match.group(4))  # us per item
    counts = {match.group(1): float(match.group(2)) for match in re.finditer(r"(\w+)=([\d.]+)", counts_text)}
    info = fields(head)
    return {"items_per_frame": info.get("items_per_frame", 0), "us_per_item": info.get("us_per_item", 0), "steps": steps, "counts": counts}


def blocks(path):
    """One dict per DRAWCOST window: frames, draw/dispatch kinds, lite lines by kind, plan line."""
    out = []
    current = None
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            if "[PERF][DRAWCOST] window_s=" in line:
                current = {"frames": fields(line).get("frames", 0), "drawcost": {}, "lite": {}, "plan": None}
                out.append(current)
            elif current is None:
                continue
            elif "[PERF][DRAWCOST] kind=" in line:
                kind = "draw" if "kind=draw " in line else "dispatch"
                current["drawcost"][kind] = drawcost_kind(line[line.index("[PERF][DRAWCOST]"):])
            elif "[PERF][LITE_OBTAIN]" in line:
                info = fields(line[line.index("[PERF][LITE_OBTAIN]"):])
                current["lite"][info.get("kind", "draw")] = info
            elif "[PERF][PLAN]" in line:
                current["plan"] = fields(line[line.index("[PERF][PLAN]"):])
    return out


def in_game(block):
    draw = block["drawcost"].get("draw")
    return 0 < block["frames"] < 60 and draw is not None and draw["items_per_frame"] > 2000


def weighted(rows, getter):
    total = sum(weight for weight, _ in rows)
    if total == 0:
        return 0.0
    return sum(weight * getter(row) for weight, row in rows) / total


def merge_drawcost(selected, kind):
    rows = [(b["frames"], b["drawcost"][kind]) for b in selected if kind in b["drawcost"]]
    if not rows:
        return None
    names = sorted({name for _, row in rows for name in row["steps"]})
    counters = sorted({name for _, row in rows for name in row["counts"]})
    return {
        "us_per_item": weighted(rows, lambda row: row["us_per_item"]),
        "items_per_frame": weighted(rows, lambda row: row["items_per_frame"]),
        "steps": {name: weighted(rows, lambda row, n=name: row["steps"].get(n, 0.0)) for name in names},
        "counts": {name: weighted(rows, lambda row, n=name: row["counts"].get(n, 0.0)) for name in counters},
        "windows": len(rows),
    }


def merge_lines(rows):
    """Frame-weighted mean of the numbers (and of dict members); counts named *wrong*/*mismatch* are summed."""
    if not rows:
        return None
    out = {}
    keys = {key for _, row in rows for key in row}
    for key in keys:
        values = [(weight, row[key]) for weight, row in rows if key in row]
        sample = values[0][1]
        if isinstance(sample, float):
            if "wrong" in key or "mismatch" in key or key == "evictions":
                out[key] = sum(value for _, value in values) if not key.endswith("_total") else max(value for _, value in values)
            else:
                out[key] = weighted(values, lambda value: value)
        elif isinstance(sample, dict):
            members = {member for _, value in values for member in value}
            out[key] = {member: weighted(values, lambda value, m=member: value.get(m, 0.0)) for member in members}
        else:
            out[key] = sample
    out["windows"] = len(rows)
    return out


def a1_saving(lite, drawcost):
    """us per item the light obtain and lookup would save; and its parts."""
    if lite is None:
        return 0.0, {}
    fast = lite.get("fast", 0.0) / 100.0
    obtains = lite.get("obtains_per_item", 0.0)
    obtain_saving = obtains * fast * max(0.0, lite.get("real_fast_ns", 0.0) - lite.get("lite_fast_ns", 0.0)) / 1000.0
    # The doc divides bind_memo_buffers by memo_buffers_changed (stages whose V#s moved); FindBuffers resolves every V#
    # of such a stage, so the per-lookup cost divides by the lookups counted (finds/item) when the probe gives them,
    # and the saving is capped at the step it comes out of.
    memo_buffers = drawcost["steps"].get("bind_memo_buffers", 0.0) if drawcost else 0.0
    changed = drawcost["counts"].get("memo_buffers_changed", 0.0) if drawcost else 0.0
    finds = lite.get("finds_per_item", 0.0)
    lookups = finds if finds > 0 else changed
    find_ns = memo_buffers * 1000.0 / lookups if lookups > 0 else 0.0
    find_fast = lite.get("find_fast", 0.0) / 100.0
    find_saving = min(memo_buffers, finds * find_fast * max(0.0, find_ns - lite.get("find_lite_ns", 0.0)) / 1000.0)
    return obtain_saving + find_saving, {
        "obtains_per_item": obtains, "fast": fast * 100, "real_fast_ns": lite.get("real_fast_ns", 0.0),
        "lite_fast_ns": lite.get("lite_fast_ns", 0.0), "obtain_saving": obtain_saving, "finds_per_item": finds,
        "find_fast": find_fast * 100, "find_ns": find_ns, "find_lite_ns": lite.get("find_lite_ns", 0.0), "find_saving": find_saving,
    }


def project(drawcost, lite, plan):
    steps = drawcost["steps"]
    today = sum(steps.values())
    equal = plan.get("state_equal", {}) if plan else {}
    eq_targets = equal.get("targets", 0.0) / 100.0
    eq_pipeline = equal.get("pipeline", 0.0) / 100.0
    eq_dynamic = equal.get("dynamic", 0.0) / 100.0
    run_start = plan.get("run_start_share", 1.0) if plan else 1.0
    gate_us = (plan.get("gate_ns", 0.0) if plan else 0.0) / 1000.0
    covered = (plan.get("covered", 0.0) if plan else 0.0) / 100.0

    parts = {}
    targets_today = sum(steps.get(name, 0.0) for name in TARGET_STEPS)
    parts["targets"] = (targets_today, (1 - eq_targets) * targets_today + eq_targets * TARGETS_EQUAL_US +
                        run_start * steps.get("draw_attachments", 0.0))
    pipeline_today = steps.get("draw_pipeline", 0.0)
    parts["pipeline"] = (pipeline_today, eq_pipeline * PIPELINE_EQUAL_US + (1 - eq_pipeline) * pipeline_today)
    dynamic_today = sum(steps.get(name, 0.0) for name in DYNAMIC_STEPS)
    parts["dynamic"] = (dynamic_today, eq_dynamic * DYNAMIC_EQUAL_US + (1 - eq_dynamic) * dynamic_today)
    buffers_today = sum(steps.get(name, 0.0) for name in BUFFER_STEPS)
    parts["buffers"] = (buffers_today, buffers_today)
    other_today = other_fast = 0.0
    for name, value in steps.items():
        if name in TARGET_STEPS or name in DYNAMIC_STEPS or name in BUFFER_STEPS or name == "draw_pipeline":
            continue
        other_today += value
        if name in ANNEX and ANNEX[name][0] > 0:
            other_fast += value * ANNEX[name][1] / ANNEX[name][0]
        else:
            other_fast += value
    parts["other"] = (other_today, other_fast)
    saving, saving_parts = a1_saving(lite, drawcost)
    fast = sum(value for _, value in parts.values()) + gate_us - saving
    mean = covered * fast + (1 - covered) * (today + gate_us)
    mean_cal = today - 0.6 * (today - mean)
    return {
        "today": today, "us_per_item": drawcost["us_per_item"], "parts": parts, "saving_a1": saving, "saving_parts": saving_parts,
        "gate_us": gate_us, "covered": covered * 100, "eq": (eq_targets * 100, eq_pipeline * 100, eq_dynamic * 100),
        "run_start_share": run_start, "fast": fast, "mean": mean, "mean_cal": mean_cal,
    }


def verdict(projection, lite_draw, lite_dispatch, plan):
    checks = []
    fast_wrong = sum((lite or {}).get(key, 0.0) for lite in (lite_draw, lite_dispatch) for key in ("fast_wrong", "find_wrong"))
    mismatch = (plan or {}).get("pipeline_key_mismatch", 0.0)
    top50 = (plan or {}).get("top50", 0.0)
    gate_ns = (plan or {}).get("gate_ns", 0.0)
    checks.append(("fast_wrong + find_wrong = 0", fast_wrong == 0, f"{fast_wrong:.0f}"))
    checks.append(("pipeline_key_mismatch = 0", mismatch == 0, f"{mismatch:.0f}"))
    checks.append(("mean_cal <= 13 us", projection["mean_cal"] <= 13, f"{projection['mean_cal']:.2f}"))
    checks.append(("mean <= 11 us", projection["mean"] <= 11, f"{projection['mean']:.2f}"))
    checks.append(("top50 >= 85 %", top50 >= 85, f"{top50:.1f}"))
    checks.append(("gate_ns <= 1000", gate_ns <= 1000, f"{gate_ns:.0f}"))
    kill = []
    if fast_wrong > 0:
        kill.append("fast_wrong")
    if mismatch > 0:
        kill.append("pipeline_key_mismatch")
    if projection["mean"] > 14:
        kill.append("mean > 14 us")
    if top50 < 75:
        kill.append("top50 < 75 %")
    if kill:
        decision = "KILL (" + ", ".join(kill) + ")"
    elif all(passed for _, passed, _ in checks):
        decision = "GO (A-1, then B)"
    else:
        decision = "GREY (A-1 only, measure before committing B)"
    return decision, checks


def main(argv):
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return 2
    path = argv[0]
    base = argv[argv.index("--base") + 1] if "--base" in argv else None
    show_windows = "--windows" in argv

    probed = [block for block in blocks(path) if in_game(block)]
    if not probed:
        print(f"{path}: no in-game DRAWCOST window (0 < frames < 60, draw items_per_frame > 2000)")
        return 1
    timed = [block for block in blocks(base) if in_game(block)] if base else probed
    if not timed:
        print(f"{base}: no in-game DRAWCOST window")
        return 1

    draw = merge_drawcost(timed, "draw")
    dispatch = merge_drawcost(timed, "dispatch")
    lite_draw = merge_lines([(b["frames"], b["lite"]["draw"]) for b in probed if "draw" in b["lite"]])
    lite_dispatch = merge_lines([(b["frames"], b["lite"]["dispatch"]) for b in probed if "dispatch" in b["lite"]])
    plan = merge_lines([(b["frames"], b["plan"]) for b in probed if b["plan"]])

    print(f"probed run {path}: {len(probed)} in-game windows, {sum(b['frames'] for b in probed):.0f} frames"
          f"; DRAWCOST steps from {'base ' + base if base else 'the probed run (probe cost included)'}: {draw['windows']} windows")
    if lite_draw is None:
        print("  no [PERF][LITE_OBTAIN] lines: the A-1 saving is 0")
    if plan is None:
        print("  no [PERF][PLAN] lines: no plan coverage")

    if show_windows:
        print("\nper window: frames draws/f us/draw | fast% wrong lite/real_fast_ns | top50 covered eq(t/p/d) mismatch gate_ns")
        for block in probed:
            d = block["drawcost"]["draw"]
            lite = block["lite"].get("draw", {})
            p = block["plan"] or {}
            eq = p.get("state_equal", {})
            print(f"  {block['frames']:4.0f} {d['items_per_frame']:6.0f} {d['us_per_item']:6.2f} | {lite.get('fast', 0):5.1f} "
                  f"{lite.get('fast_wrong', 0):3.0f} {lite.get('lite_fast_ns', 0):4.0f}/{lite.get('real_fast_ns', 0):4.0f} | "
                  f"{p.get('top50', 0):5.1f} {p.get('covered', 0):5.1f} {eq.get('targets', 0):5.1f}/{eq.get('pipeline', 0):5.1f}/{eq.get('dynamic', 0):5.1f} "
                  f"{p.get('pipeline_key_mismatch', 0):3.0f} {p.get('gate_ns', 0):5.0f}")

    projection = project(draw, lite_draw, plan)
    print(f"\n== draws: {draw['items_per_frame']:.0f}/frame, today {projection['today']:.2f} us/draw (us_per_item {projection['us_per_item']:.2f})")
    for name, (today, fast) in projection["parts"].items():
        print(f"  {name:9s} today {today:6.2f}  plan {fast:6.2f}")
    sp = projection["saving_parts"]
    if sp:
        print(f"  A-1 saving {projection['saving_a1']:.2f} us = obtains {sp['obtains_per_item']:.2f}/draw x fast {sp['fast']:.1f}% x "
              f"({sp['real_fast_ns']:.0f} - {sp['lite_fast_ns']:.0f}) ns [{sp['obtain_saving']:.2f}] + finds {sp['finds_per_item']:.2f} x "
              f"{sp['find_fast']:.1f}% x ({sp['find_ns']:.0f} - {sp['find_lite_ns']:.0f}) ns [{sp['find_saving']:.2f}]")
    eq = projection["eq"]
    print(f"  state_equal targets/pipeline/dynamic {eq[0]:.1f}/{eq[1]:.1f}/{eq[2]:.1f} %, run_start_share {projection['run_start_share']:.3f}, "
          f"gate {projection['gate_us'] * 1000:.0f} ns, covered {projection['covered']:.1f} %")
    print(f"  fast path {projection['fast']:.2f} us/draw   mean {projection['mean']:.2f}   mean_cal {projection['mean_cal']:.2f}")
    if plan:
        sc = plan.get("slots_changed", {})
        rl = plan.get("run_len", {})
        print(f"  shapes {plan.get('shapes', 0):.0f}/window ({plan.get('shapes_per_frame', 0):.0f}/frame) top10/50/200 {plan.get('top10', 0):.1f}/"
              f"{plan.get('top50', 0):.1f}/{plan.get('top200', 0):.1f} %, store_hit {plan.get('store_hit', 0):.1f} %, rt {plan.get('rt', 0):.1f} %, "
              f"unsupported {plan.get('unsupported', 0):.1f} %")
        print(f"  slots_changed 0/1/2/3+ {sc.get('0', 0):.1f}/{sc.get('1', 0):.1f}/{sc.get('2', 0):.1f}/{sc.get('3+', 0):.1f} %, "
              f"run_len p50/p90 {rl.get('p50', 0):.0f}/{rl.get('p90', 0):.0f}, draws in runs >= 16 {plan.get('draws_in_runs16', 0):.1f} %")
        print(f"  exactness: pipeline_key_mismatch {plan.get('pipeline_key_mismatch', 0):.0f} (checked {plan.get('pipeline_checked', 0):.1f} % of hits, "
              f"rendering_changed {plan.get('rendering_changed', 0):.1f} %), dynamic_mismatch {plan.get('dynamic_mismatch', 0):.0f} "
              f"(checked {plan.get('dynamic_checked', 0):.1f} %)")
    if lite_draw:
        slow = lite_draw.get("slow", {})
        print(f"  lite obtain: fast {lite_draw.get('fast', 0):.1f} %, fast_wrong {lite_draw.get('fast_wrong', 0):.0f}, find_wrong "
              f"{lite_draw.get('find_wrong', 0):.0f}, slow " + ",".join(f"{k}:{v:.1f}" for k, v in sorted(slow.items())) +
              f", slow_clean {lite_draw.get('slow_clean', 0):.1f} %, lite/real {lite_draw.get('lite_ns', 0):.0f}/{lite_draw.get('real_ns', 0):.0f} ns")
        print(f"  A-2: eligible_raw {lite_draw.get('eligible_raw', 0):.1f} % of non-RT draw buffer slots (GO for A-2 at >= 85), "
              f"rt_excluded {lite_draw.get('rt_excluded', 0):.1f} %")

    if dispatch:
        saving, parts = a1_saving(lite_dispatch, dispatch)
        print(f"\n== dispatches: {dispatch['items_per_frame']:.0f}/frame, today {dispatch['us_per_item']:.2f} us/dispatch; "
              f"A-1 alone saves {saving:.2f} -> {dispatch['us_per_item'] - saving:.2f} us/dispatch")
        if parts:
            print(f"  obtains {parts['obtains_per_item']:.2f} x fast {parts['fast']:.1f}% x ({parts['real_fast_ns']:.0f} - {parts['lite_fast_ns']:.0f}) ns, "
                  f"finds {parts['finds_per_item']:.2f} x {parts['find_fast']:.1f}% x ({parts['find_ns']:.0f} - {parts['find_lite_ns']:.0f}) ns, "
                  f"fast_wrong {lite_dispatch.get('fast_wrong', 0):.0f}")

    decision, checks = verdict(projection, lite_draw, lite_dispatch, plan)
    print("\n== gate")
    for name, passed, value in checks:
        print(f"  [{'pass' if passed else 'FAIL'}] {name}: {value}")
    if lite_draw:
        a_light = lite_draw.get("fast", 0)
        print(f"  [{'pass' if a_light >= 80 else 'FAIL'}] (A, 3.7) >= 80 % of draw read obtains on the light path: {a_light:.1f}")
        print(f"  [{'pass' if lite_draw.get('eligible_raw', 0) >= 85 else 'FAIL'}] (A-2) eligible_raw >= 85 %: {lite_draw.get('eligible_raw', 0):.1f}")
    print(f"\n{decision}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
