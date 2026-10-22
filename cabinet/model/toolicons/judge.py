#!/usr/bin/env python3
"""Judge for the tool wheel's icons: does each baked icon (cabinet/art/tools) represent its tool?

  python3 judge.py pack N             judge/round-N: one sheet per category, each icon numbered and
                                      labelled with its tool, at wheel size and 2x; prompt.txt for
                                      the judge (with what each tool does); key.json (number -> tool)
  python3 judge.py score N v.json     check the judge's verdict against the rubric and the key,
                                      compute each icon's score here, write report.md; exit 1 while
                                      any icon is under the target

The judge is a fresh context that sees only the sheets and prompt.txt. Per icon it scores the rubric
against the tool named under it, with evidence and a fix. The score is computed here: the weighted
mean of the categories.
"""
import json
import os
import sys

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ART = os.path.normpath(os.path.join(HERE, "..", "..", "art", "tools"))
TARGET = 9.0
FLOOR = 8.0
RUBRIC = [
    ("represents", 3.0, "The icon shows what this tool does: its own idea (object or action), so a modeller "
                        "seeing it next to the name gets it instantly. Not a generic shape that could be any tool."),
    ("distinct", 2.0, "It can't be confused with any other icon in the same category (shape, colour and action differ)."),
    ("legibility", 2.0, "At wheel size (the 1x row) the silhouette and the key part read clearly: nothing important "
                        "is a lone pixel, thin parts survive, the red accent marks the action."),
    ("craft", 1.0, "Clean pixel art in the chest's style: solid outline, no muddy dithering, no stray bits, well framed."),
]
# what each tool does, for the judge (the wheel's names)
TOOLS = json.load(open(os.path.join(HERE, "tools.json")))


def groups():
    cells = json.load(open(os.path.join(HERE, "cells.json")))["icons"]
    out = {}
    for key, c in cells.items():
        out.setdefault(c["group"], []).append((key, c["label"]))
    return out


def pack(n, only=None):
    """only: score just these keys; the rest of each group is still drawn, unnumbered, for comparison"""
    d = os.path.join(HERE, "judge", "round-%d" % n)
    os.makedirs(d, exist_ok=True)
    key, lines = {}, []
    num = 1
    for group, tools in groups().items():
        order = tools[:]
        cs, pad = 140, 10
        sheet = Image.new("RGB", (len(order) * cs, 230), "#2b2f36")
        dr = ImageDraw.Draw(sheet)
        names = []
        for i, (k, label) in enumerate(order):
            im = Image.open(os.path.join(ART, k.replace(":", "_") + ".png")).convert("RGBA")
            x = i * cs
            scored = not only or k in only
            dr.text((x + pad, 6), ("#%d %s" % (num, label.upper())) if scored else label.upper(), fill="#f2a91c" if scored else "#8a8f98")
            sheet.paste(im, (x + (cs - im.width) // 2, 24), im)                       # 1x, as on the wheel
            big = im.resize((im.width * 2, im.height * 2), Image.NEAREST)
            sheet.paste(big, (x + (cs - big.width) // 2, 24 + im.height + 8), big)   # 2x
            if scored:
                key[str(num)] = k
                num += 1
                names.append((k, label))
        path = os.path.join(d, "%s.png" % group.lower())
        sheet.save(path)
        first = num - len(names)
        rest = [lab for k, lab in order if (k, lab) not in names]
        lines.append("## %s\nSheet: %s\n%s\n%s" % (group, path, "\n".join(
            "- #%d %s: %s" % (first + i, lab, TOOLS[k]) for i, (k, lab) in enumerate(names)),
            ("(Unnumbered, grey-labelled icons on this sheet are context only: don't score them, but judge distinctness against them: %s)\n" % ", ".join(rest)) if rest else ""))
    json.dump(key, open(os.path.join(d, "key.json"), "w"), indent=1)
    rubric = "\n".join("- %s (weight %g): %s" % r for r in RUBRIC)
    prompt = """You are judging icons for a 3D modelling app's tool wheel. Each icon is pixel art baked from a small
3D model. They sit on a dark steel wheel at 1x (the top row of each sheet); the 2x row below is the same
icon enlarged so you can see it. Each icon is numbered and labelled with its tool; below is what each
tool does. Judge how well each icon represents its tool.

For every numbered icon, score each rubric category 0-10 with the evidence you actually see in the
picture, and a concrete fix (what to change in the 3D object: add/remove/move/recolour a part). Empty
fix only for a 10.

Be strict. 9 means a modeller would get it instantly and nothing needs changing; 7 means it works but
something is weak; 5 or less means wrong or unreadable. Do not be generous to be nice.

Rubric:
%s

%s
Look at every sheet with the Read tool. Reply with ONLY this JSON (no prose), every icon number once:
{"icons": [{"n": 1, "scores": {"represents": {"score": 0, "evidence": "", "fix": ""},
 "distinct": {...}, "legibility": {...}, "craft": {...}}}, ...]}
""" % (rubric, "\n".join(lines))
    open(os.path.join(d, "prompt.txt"), "w").write(prompt)
    print(os.path.join(d, "prompt.txt"))


def score(n, verdict):
    d = os.path.join(HERE, "judge", "round-%d" % n)
    key = json.load(open(os.path.join(d, "key.json")))
    cells = json.load(open(os.path.join(HERE, "cells.json")))["icons"]
    text = open(verdict).read().strip()
    if text.startswith("```"):
        text = text.split("\n", 1)[1].rsplit("```", 1)[0]
    v = json.loads(text)
    seen, rows = set(), []
    for e in v["icons"]:
        num = str(e["n"])
        if num not in key or num in seen:
            raise SystemExit("verdict: icon #%s unknown or twice" % num)
        seen.add(num)
        k = key[num]
        total, wsum, lowest, notes = 0.0, 0.0, 10.0, []
        for name, w, _ in RUBRIC:
            s = e["scores"][name]
            sc = float(s["score"])
            if not 0 <= sc <= 10 or not str(s.get("evidence", "")).strip():
                raise SystemExit("verdict: #%s %s needs a 0-10 score with evidence" % (num, name))
            if sc < 10 and not str(s.get("fix", "")).strip():
                raise SystemExit("verdict: #%s %s scored under 10 with no fix" % (num, name))
            total += sc * w
            wsum += w
            lowest = min(lowest, sc)
            if s.get("fix"):
                notes.append("%s %g: %s -> %s" % (name, sc, s["evidence"], s["fix"]))
        total /= wsum
        ok = total >= TARGET and lowest >= FLOOR
        rows.append((total, k, cells[k]["label"], ok, notes))
    missing = set(key) - seen
    if missing:
        raise SystemExit("verdict: no score for icons %s" % ", ".join(sorted(missing, key=int)))
    rows.sort()
    out = ["# Tool icons, round %d" % n, "",
           "%d / %d pass (score >= %g, every category >= %g). Mean %.2f." % (
               sum(r[3] for r in rows), len(rows), TARGET, FLOOR, sum(r[0] for r in rows) / len(rows)), ""]
    for total, k, label, ok, notes in rows:
        out.append("- %s **%s** %.2f" % ("PASS" if ok else "FAIL", label, total))
        if not ok:
            out += ["  - " + x for x in notes]
    open(os.path.join(d, "report.md"), "w").write("\n".join(out) + "\n")
    json.dump({k: {"score": t, "pass": ok, "notes": nt} for t, k, _, ok, nt in rows},
              open(os.path.join(d, "scores.json"), "w"), indent=1)
    print("\n".join(out[:3]))
    sys.exit(0 if all(r[3] for r in rows) else 1)


if __name__ == "__main__":
    if sys.argv[1] == "pack":
        pack(int(sys.argv[2]), set(sys.argv[3][5:].split(",")) if len(sys.argv) > 3 and sys.argv[3].startswith("only=") else None)
    else:
        score(int(sys.argv[2]), sys.argv[3])
