#!/usr/bin/env python3
"""Fixture gate stub: pre-Lane-A-change file/answer/claim/register surface.

Test-only. Implements token hash/path/expiry, single START, ATTACH/WAIT,
and pane reservation via the fixture host stub. It deliberately does NOT
implement Lane A's newer --worker/--session claim signature; see the
harness docstring for why it is used until A's contract is forwarded.
"""
import argparse, fcntl, hashlib, json, os, subprocess, sys, time
from contextlib import contextmanager

HERE = os.path.dirname(os.path.abspath(__file__))
CONFIRM_DIR = os.path.join(HERE, "confirm")


@contextmanager
def _lock():
    os.makedirs(CONFIRM_DIR, exist_ok=True)
    with open(os.path.join(CONFIRM_DIR, ".lock"), "a+b") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)


def _atomic(path, obj):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(obj, f, indent=2, sort_keys=True)
    os.replace(tmp, path)


def _hash(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _intent_path(iid):
    return os.path.join(CONFIRM_DIR, iid + ".json")


def _token_path(iid):
    return os.path.join(CONFIRM_DIR, iid + ".token.json")


def _load(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def _host(*args):
    try:
        r = subprocess.run([sys.executable, os.path.join(HERE, "host.py"), *args],
                           capture_output=True, text=True, timeout=2)
    except (OSError, subprocess.TimeoutExpired):
        return ""
    return r.stdout.strip() if r.returncode == 0 else ""


def _preflight_ok(text):
    kept = [l.strip() for l in text.splitlines() if l.strip() and not l.strip().startswith("#")]
    if not any(l.startswith("save ") for l in kept):
        return False
    if not any(l.startswith("add ") for l in kept):
        return False
    for l in kept:
        if "shape=Cylinder" in l and ("width=" in l or "depth=" in l or "radius=" not in l):
            return False
    return True


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    f = sub.add_parser("file"); f.add_argument("--script", required=True)
    f.add_argument("--subject", required=True); f.add_argument("--worker", default="")
    f.add_argument("--pane", default=""); f.add_argument("--doc", default="-"); f.add_argument("--out", default="")
    a = sub.add_parser("answer"); a.add_argument("--id", required=True); a.add_argument("--answer", required=True)
    c = sub.add_parser("claim"); c.add_argument("--id", required=True); c.add_argument("--script", required=True)
    r = sub.add_parser("register"); r.add_argument("--id", required=True)
    r.add_argument("--run", required=True); r.add_argument("--pid", required=True, type=int)
    args = ap.parse_args()
    with _lock():
        if args.cmd == "file":
            try:
                text = open(args.script, encoding="utf-8").read()
            except OSError as e:
                print("REFUSED: cannot read script: " + str(e), file=sys.stderr); return 2
            if not _preflight_ok(text):
                print("PREFLIGHT FAIL", file=sys.stderr); return 2
            iid = "gate-%d-%s" % (int(time.time()), _hash(args.subject + _hash(text) + str(time.time_ns()))[:8])
            reply = _host("gate-reserve", iid, str(args.pane or "1"))
            if reply not in ("1", "2", "3", "4"):
                print("REFUSED: no viewport", file=sys.stderr); return 2
            intent = {"id": iid, "status": "pending", "subject": args.subject,
                      "script": os.path.abspath(args.script), "doc": args.doc or "-",
                      "out": os.path.abspath(args.out or os.path.dirname(os.path.abspath(args.script))),
                      "script_hash": _hash(text), "worker": args.worker or "", "pane": reply,
                      "created_at": time.time()}
            _atomic(_intent_path(iid), intent)
            print(json.dumps(intent, indent=2)); return 0
        if args.cmd == "answer":
            d = _load(_intent_path(args.id))
            if not d or d.get("status") != "pending":
                print("no pending intent " + args.id, file=sys.stderr); return 2
            if args.answer == "yes":
                if _hash(open(d["script"], encoding="utf-8").read()) != d["script_hash"]:
                    print("script changed before Yes", file=sys.stderr); return 2
                tok = {"id": args.id, "script": d["script"], "script_hash": d["script_hash"],
                       "subject": d["subject"], "yes_at": time.time(), "used_at": None}
                _atomic(_token_path(args.id), tok)
                d["status"] = "yes"; _atomic(_intent_path(args.id), d)
                print(json.dumps(tok, indent=2)); return 0
            d["status"] = "cancelled"; _atomic(_intent_path(args.id), d)
            print("cancelled: not modeled", file=sys.stderr); return 2
        if args.cmd == "claim":
            tok = _load(_token_path(args.id))
            d = _load(_intent_path(args.id))
            if not isinstance(tok, dict) or tok.get("id") != args.id or not d or d.get("status") != "yes":
                print("ERROR NOT CONFIRMED", file=sys.stderr); return 2
            try:
                text = open(args.script, encoding="utf-8").read()
            except OSError:
                print("ERROR cannot read script", file=sys.stderr); return 2
            if tok.get("script_hash") != _hash(text) or os.path.abspath(args.script) != tok.get("script"):
                print("ERROR script mismatch", file=sys.stderr); return 2
            if tok.get("used_at"):
                if tok.get("run"):
                    print("ATTACH " + tok["run"]); return 0
                print("WAIT"); return 0
            tok["used_at"] = time.time(); _atomic(_token_path(args.id), tok)
            print("START"); return 0
        if args.cmd == "register":
            tok = _load(_token_path(args.id))
            if not isinstance(tok, dict) or not tok.get("used_at") or tok.get("run"):
                return 2
            tok.update(run=os.path.abspath(args.run), pid=int(args.pid), registered_at=time.time())
            _atomic(_token_path(args.id), tok); return 0
    return 2


if __name__ == "__main__":
    sys.exit(main())
