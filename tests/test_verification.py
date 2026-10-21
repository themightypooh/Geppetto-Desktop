"""Orchestrator fault-injection tests; no dotnet build or graphics context is opened here."""
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest

from verify import Verification


class FakeCommands:
    def __init__(self, *, build_exit=0, gpu_exit=0, software=False, wrong_output=False):
        self.calls = []
        self.build_exit = build_exit
        self.gpu_exit = gpu_exit
        self.software = software
        self.wrong_output = wrong_output

    def __call__(self, args, **kwargs):
        self.calls.append(args)
        if args[1] == "build":
            if self.build_exit:
                return subprocess.CompletedProcess(args, self.build_exit)
            build = Path(args[args.index("--artifacts-path") + 1])
            self.build = build
            if not self.wrong_output:
                for relative in ("Desktop.Tests/release/Desktop.Tests.dll",
                                 "Marionette.Desktop/release/marionette-desktop.dll",
                                 "Effigy.Kernel/release/Effigy.Kernel.dll"):
                    target = build / "bin" / relative
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(relative.encode())
        elif "--report" in args:
            assemblies = []
            for path in self.build.glob("bin/*/release/*.dll"):
                assemblies.append({"path": str(path), "name": path.stem,
                                   "mvid": "00000000-0000-0000-0000-000000000001",
                                   "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
            result = {"Group": "synthetic", "Name": "sentinel", "Status": "PASS"}
            data = {"mode": "cpu", "selected": 1, "registered": 1,
                    "passed": 1, "failed": 0, "skipped": 0, "results": [result],
                    "inventory": [result], "assemblies": assemblies}
            Path(args[args.index("--report") + 1]).write_text(json.dumps(data))
        elif "--headless" in args:
            assert "DISPLAY" not in kwargs["env"] and "WAYLAND_DISPLAY" not in kwargs["env"]
            if self.gpu_exit:
                kwargs["stdout"].write("EGL unavailable; no window will be opened\n")
                return subprocess.CompletedProcess(args, self.gpu_exit)
            kwargs["stdout"].write("headless : EGL device/pbuffer; no window or display-server connection\n")
            kwargs["stdout"].write("renderer : " + ("llvmpipe" if self.software else "fake test GPU") + "\n")
            kwargs["stdout"].write("headless : drew 120 frame(s) in 0.1s\n")
            # Stub header only: testing orchestration/dimension checks, not PNG decoder correctness.
            shot = b"\x89PNG\r\n\x1a\n" + b"\0" * 8 + struct.pack(">II", 1024, 1024)
            shot += b"animated" if "--animate" in args else b"bind"
            Path(args[args.index("--screenshot") + 1]).write_bytes(shot)
        return subprocess.CompletedProcess(args, 0)


class VerificationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="geppetto-harness-selftest-", dir="/tmp/opencode")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.survey = self.root / "fixture/player_suit.fbx"
        self.survey.parent.mkdir()
        self.survey.write_bytes(b"fixture")
        self.survey.with_name("player_head.fbx").write_bytes(b"fixture")

    def run_verification(self, fake, mode="cpu"):
        runner = Verification(self.root / "run", mode=mode, survey=self.survey, execute=fake)
        code = runner.run()
        return code, json.loads((runner.output / "verification.json").read_text())

    def test_failed_build_never_executes_preexisting_dll(self):
        stale = self.root / "run/build/bin/Desktop.Tests/release/Desktop.Tests.dll"
        stale.parent.mkdir(parents=True)
        stale.write_bytes(b"stale artifact must never execute")
        fake = FakeCommands(build_exit=1)
        code, report = self.run_verification(fake)
        self.assertEqual(code, 1)
        self.assertEqual(len(fake.calls), 1)
        self.assertEqual(report["status"], "FAIL")
        self.assertEqual(stale.read_bytes(), b"stale artifact must never execute")

    def test_wrong_build_output_is_rejected_before_execution(self):
        fake = FakeCommands(wrong_output=True)
        code, _ = self.run_verification(fake)
        self.assertEqual(code, 1)
        self.assertEqual(len(fake.calls), 1)

    def test_cpu_mode_has_no_gpu_calls_or_gpu_pass_claim(self):
        fake = FakeCommands()
        code, report = self.run_verification(fake)
        self.assertEqual(code, 0)
        self.assertFalse(any("--headless" in c for c in fake.calls))
        self.assertEqual(report["gpu"]["status"], "NOT_REQUESTED")
        self.assertIn("--artifacts-path", fake.calls[0])

    def test_required_egl_failure_never_falls_back(self):
        fake = FakeCommands(gpu_exit=1)
        code, report = self.run_verification(fake, "required-egl")
        self.assertEqual(code, 1)
        self.assertEqual(report["gpu"]["status"], "FAIL")
        self.assertEqual(len([c for c in fake.calls if "--headless" in c]), 1)
        self.assertTrue(all("--headless" in c for c in fake.calls if "--screenshot" in c))

    def test_required_gpu_missing_fixture_is_not_skipped(self):
        self.survey.with_name("player_head.fbx").unlink()
        fake = FakeCommands()
        code, report = self.run_verification(fake, "required-egl")
        self.assertEqual(code, 1)
        self.assertEqual(report["gpu"]["status"], "FAIL")
        self.assertIn("Required GPU fixture missing", report["reason"])
        self.assertFalse(any("--headless" in c for c in fake.calls))

    def test_software_renderer_cannot_pass_hardware_gate(self):
        code, report = self.run_verification(FakeCommands(software=True), "required-egl")
        self.assertEqual(code, 1)
        self.assertIn("hardware EGL", report["reason"])

    def test_successful_hardware_flow_records_each_capture(self):
        code, report = self.run_verification(FakeCommands(), "required-egl")
        self.assertEqual(code, 0)
        self.assertEqual(report["gpu"]["status"], "PASS")
        self.assertEqual(len(report["gpu"]["checks"]), 3)
        self.assertEqual(len(report["assemblies"]), 3)

    def test_incomplete_cpu_inventory_cannot_be_green(self):
        runner = Verification(self.root / "run")
        path = self.root / "bad-report.json"
        path.write_text(json.dumps({"mode": "cpu", "selected": 0, "registered": 30, "results": []}))
        with self.assertRaises(Exception):
            runner.validate_cpu_report(path)


if __name__ == "__main__":
    unittest.main()
