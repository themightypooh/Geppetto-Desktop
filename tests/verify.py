#!/usr/bin/env python3
"""Fail-closed desktop verification; CPU by default, explicit required hardware EGL.

No dependency on third-party Python packages. Build output is isolated per run/project.
Never executes stale artifacts after a build failure and never opens a window.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import struct
import subprocess
import sys
import tempfile
import time
import uuid
from datetime import datetime, timezone

PROJECT = Path(__file__).resolve().parent.parent
SURVEY = Path("/home/poo/games/projects/deep_sentance/Assets/models/player/player_suit.fbx")


class VerificationError(Exception):
    pass


def identity(path):
    path = Path(path).resolve()
    return {"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


class Verification:
    def __init__(self, output, *, mode="cpu", survey=SURVEY, require_local=False,
                 project=PROJECT, execute=subprocess.run):
        if mode not in ("cpu", "required-egl"):
            raise ValueError("Unknown verification mode")
        self.output = Path(output).resolve()
        self.project = Path(project).resolve()
        self.execute = execute
        self.survey = Path(survey).resolve()
        self.require_local = require_local
        self.environment = {k: v for k, v in os.environ.items()
                            if k not in ("DISPLAY", "WAYLAND_DISPLAY")}
        if mode == "required-egl":
            self.environment["V3_REQUIRE_EGL"] = "1"
            self.environment["V4_REQUIRE_EGL"] = "1"
        else:
            self.environment.pop("V3_REQUIRE_EGL", None)
            self.environment.pop("V4_REQUIRE_EGL", None)
        self.report = {
            "schemaVersion": 1, "mode": mode, "status": "RUNNING",
            "createdUtc": datetime.now(timezone.utc).isoformat(),
            "project": str(self.project), "artifacts": str(self.output),
            "platform": platform.platform(), "python": sys.version,
            "fixture": str(self.survey), "requireLocalFixtures": require_local,
            "stages": [], "assemblies": [],
            "gpu": {"status": "NOT_RUN" if mode == "required-egl" else "NOT_REQUESTED",
                    "reason": None if mode == "required-egl" else "CPU-only mode; no GPU coverage claimed"},
        }

    def command(self, name, args, timeout=120):
        stage = {"name": name, "command": [str(a) for a in args],
                 "cwd": str(self.project), "status": "RUNNING",
                 "log": str(self.output / (name + ".log"))}
        self.report["stages"].append(stage)
        start = time.monotonic()
        try:
            with open(stage["log"], "w") as log:
                result = self.execute(stage["command"], cwd=self.project,
                                      stdout=log, stderr=subprocess.STDOUT,
                                      timeout=timeout, env=self.environment)
            stage["exitCode"] = result.returncode
            if result.returncode != 0:
                raise VerificationError(f"{name} failed (exit {result.returncode}); see {stage['log']}")
            stage["status"] = "PASS"
        except Exception as error:
            stage["status"] = "FAIL"
            stage["reason"] = str(error)
            raise
        finally:
            stage["wallSeconds"] = time.monotonic() - start

    def validate_cpu_report(self, path):
        report_path = path
        data = json.loads(path.read_text())
        results = data["results"]
        counts = {status: sum(r["Status"] == status for r in results)
                  for status in ("PASS", "FAIL", "SKIP")}
        if (data["mode"] != "cpu" or data["selected"] <= 0
                or data["selected"] != data["registered"]
                or len(results) != data["selected"]
                or len(data["inventory"]) != data["registered"]
                or len({(r["Group"], r["Name"]) for r in results}) != len(results)
                or counts["FAIL"] != 0
                or counts["PASS"] + counts["SKIP"] != len(results)
                or (data["passed"], data["failed"], data["skipped"])
                != (counts["PASS"], counts["FAIL"], counts["SKIP"])):
            raise VerificationError("Invalid, incomplete or failing CPU test report")
        if self.require_local and counts["SKIP"]:
            raise VerificationError("Required local fixtures were skipped")
        expected = {(r["Group"], r["Name"]) for r in data["inventory"]}
        if expected != {(r["Group"], r["Name"]) for r in results}:
            raise VerificationError("CPU test results do not match the registered inventory")
        expected_assemblies = {"Desktop.Tests", "marionette-desktop", "Effigy.Kernel"}
        if len(data["assemblies"]) != 3 or {a["name"] for a in data["assemblies"]} != expected_assemblies:
            raise VerificationError("CPU report must identify the test, app and kernel assemblies")
        for assembly in data["assemblies"]:
            uuid.UUID(assembly["mvid"])
            path = Path(assembly["path"]).resolve()
            if not path.is_relative_to(self.output / "build"):
                raise VerificationError("CPU tests used an assembly outside this run's isolated build")
            if identity(path)["sha256"].lower() != assembly["sha256"].lower():
                raise VerificationError("CPU assembly identity mismatch")
        self.report["cpu"] = {"report": str(report_path),
                              "passed": data["passed"], "failed": data["failed"],
                              "skipped": data["skipped"], "registered": data["registered"]}

    def gpu(self, dll):
        self.report["gpu"] = {"status": "RUNNING", "checks": [], "fixture": str(self.survey)}
        required = (self.survey, self.survey.with_name("player_head.fbx"))
        missing = [str(p) for p in required if not p.is_file()]
        if missing:
            raise VerificationError("Required GPU fixture missing: " + ", ".join(missing))
        hashes = {}
        for name, extra in (("bind-a", []), ("bind-b", []),
                            ("animated", ["--animate", "--time", "0.5"])):
            shot = self.output / (name + ".png")
            self.command(name, ["dotnet", dll, "--headless", "--model", self.survey,
                                "--frames", "120", "--size", "1024x1024",
                                "--screenshot", shot, *extra], timeout=60)
            log = (self.output / (name + ".log")).read_text()
            if "headless : EGL device/pbuffer; no window or display-server connection" not in log:
                raise VerificationError("GPU check did not report a windowless EGL context")
            if "headless : drew 120 frame(s)" not in log:
                raise VerificationError("GPU check did not complete its requested frame count")
            renderer = next((s for s in log.splitlines() if s.startswith("renderer : ")), "")
            if not renderer or any(word in renderer.lower() for word in ("llvmpipe", "softpipe", "swrast", "software rasterizer")):
                raise VerificationError("Required hardware EGL renderer was unavailable")
            data = shot.read_bytes()
            if data[:8] != b"\x89PNG\r\n\x1a\n" or len(data) < 24 or struct.unpack(">II", data[16:24]) != (1024, 1024):
                raise VerificationError("GPU check produced a missing/invalid/wrong-size PNG")
            hashes[name] = hashlib.sha256(data).hexdigest()
            self.report["gpu"]["checks"].append({"name": name, "shot": str(shot),
                                                 "sha256": hashes[name], "renderer": renderer,
                                                 "status": "PASS"})
        if hashes["bind-a"] != hashes["bind-b"]:
            raise VerificationError("Repeated bind captures drifted")
        if hashes["bind-a"] == hashes["animated"]:
            raise VerificationError("Animated pose did not change the captured model")
        self.report["gpu"]["status"] = "PASS"

    def run(self):
        self.output.mkdir(parents=True, exist_ok=True)
        try:
            build = self.output / "build"
            # Fail closed before reading or executing any existing DLLs.
            self.command("build", ["dotnet", "build", "tests/Desktop.Tests.csproj", "-c", "Release",
                                   "--artifacts-path", build])
            tests = build / "bin/Desktop.Tests/release/Desktop.Tests.dll"
            app = build / "bin/Marionette.Desktop/release/marionette-desktop.dll"
            kernel = build / "bin/Effigy.Kernel/release/Effigy.Kernel.dll"
            self.report["assemblies"] = [identity(p) for p in (tests, app, kernel)]
            self.command("harness-selftests", [sys.executable, "-m", "unittest", "discover",
                                              "-s", "tests", "-p", "test_verification.py", "-v"])
            self.command("inventory", ["dotnet", tests, "--list", "--survey", self.survey])
            args = ["dotnet", tests, "--survey", self.survey, "--report", self.output / "cpu-results.json"]
            if self.require_local:
                args.append("--require-local-fixtures")
            try:
                self.command("cpu", args)
            finally:
                if (self.output / "cpu-results.json").is_file():
                    self.report["cpuReport"] = str(self.output / "cpu-results.json")
            self.validate_cpu_report(self.output / "cpu-results.json")
            if self.report["mode"] == "required-egl":
                self.gpu(app)
            self.report["status"] = "PASS"
            return 0
        except Exception as error:
            self.report["status"] = "FAIL"
            self.report["reason"] = str(error)
            if self.report["gpu"]["status"] == "RUNNING":
                self.report["gpu"]["status"] = "FAIL"
                self.report["gpu"]["reason"] = str(error)
            return 1
        finally:
            (self.output / "verification.json").write_text(json.dumps(self.report, indent=2))
            print(f"{self.report['status']} {self.report['mode']}: {self.output / 'verification.json'}", flush=True)
            if "reason" in self.report:
                print(self.report["reason"], file=sys.stderr, flush=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("cpu", "required-egl"), default="cpu")
    parser.add_argument("--survey", type=Path, default=SURVEY)
    parser.add_argument("--require-local-fixtures", action="store_true")
    parser.add_argument("--output-root", type=Path, default=Path("/tmp/opencode"))
    args = parser.parse_args(argv)
    args.output_root.mkdir(parents=True, exist_ok=True)
    output = tempfile.mkdtemp(prefix="geppetto-verify-", dir=args.output_root)
    return Verification(output, mode=args.mode, survey=args.survey,
                        require_local=args.require_local_fixtures).run()


if __name__ == "__main__":
    raise SystemExit(main())
