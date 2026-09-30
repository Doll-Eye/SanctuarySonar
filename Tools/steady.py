#!/usr/bin/env python3
"""Steadiness of a MapLab replay: how often the lead swings.

    Tools/steady.py Reference/<run>/replay.txt

Reads the "go <direction> (<degrees>°)" leads, one per sampled frame, and reports per floor
(split at "FLOOR n of m" lines, else the whole run):

  reversals   consecutive leads more than 90° apart
  flip-flops  a reversal undone by another reversal within 3 s — the lead went back to
              where it was, so the player was sent one way and straight back
  no-lead     frames that were legible but had no opening to lead to
  new maps    "fits nowhere" resets

The 27 Sep reference (09:36 Undercity recording, since lost) read 8 / 4, 9 / 4, 5 / 2 on
its three floors; the definitions above are the ones used then, rewritten from the notes.
"""
import re, sys

lead = re.compile(r"^\s*([\d.]+) s .*?go [a-z-]+ \(([-\d]+)°\)")
floor = re.compile(r"^\s*([\d.]+) s\s+FLOOR (\d+) of (\d+)")
noopen = re.compile(r"^\s*([\d.]+) s .*?no opening\s*$")
newmap = re.compile(r"^\s*([\d.]+) s\s+fits nowhere")

def report(name, leads, nolead, resets):
    rev = 0; flips = 0; last = None; pending = None
    for t, deg in leads:
        if last is not None:
            d = abs((deg - last[1] + 180) % 360 - 180)
            if d > 90:
                rev += 1
                if pending is not None and t - pending <= 3: flips += 1; pending = None
                else: pending = t
        last = (t, deg)
    span = f"{leads[0][0]:.0f}–{leads[-1][0]:.0f} s" if leads else "no leads"
    print(f"{name}: reversals {rev} flip-flops {flips}; {len(leads)} leads over {span}, no-lead {nolead}, new maps {resets}")
    return rev, flips

def main(path):
    floors = [("run", [], 0, 0)]
    for line in open(path, errors="replace"):
        m = floor.match(line)
        if m:
            floors.append((f"floor {m.group(2)} of {m.group(3)}", [], 0, 0)); continue
        m = lead.match(line)
        name, leads, nolead, resets = floors[-1]
        if m: leads.append((float(m.group(1)), int(m.group(2)))); continue
        if noopen.match(line): floors[-1] = (name, leads, nolead + 1, resets); continue
        if newmap.match(line): floors[-1] = (name, leads, nolead, resets + 1)
    summary = []
    for name, leads, nolead, resets in floors:
        if leads or nolead or resets: summary.append(report(name, leads, nolead, resets))
    print("summary: " + ", ".join(f"{r}/{f}" for r, f in summary))

if __name__ == "__main__":
    if len(sys.argv) < 2: print(__doc__); sys.exit(2)
    for p in sys.argv[1:]: print(f"== {p}"); main(p)
