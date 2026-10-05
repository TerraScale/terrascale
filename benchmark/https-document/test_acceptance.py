"""Pure trust-boundary checks; integration always uses the pinned real binary."""
import hashlib
import io
import json
from pathlib import Path
import tempfile
import subprocess
import sys
import unittest
from unittest.mock import patch

import acceptance


class PinTests(unittest.TestCase):
    def test_committed_pin_and_reject_untrusted_download(self):
        pin = json.loads((Path(__file__).parent / "release.json").read_text())
        acceptance.validate_pin(pin)
        for change in ({"url": "https://attacker.invalid/asset"}, {"sha256": "0"}, {"version": "../main"}):
            with self.assertRaises(ValueError):
                acceptance.validate_pin({**pin, **change})

    def test_malformed_pins_preserve_failure_evidence(self):
        pin = json.loads((Path(__file__).parent / "release.json").read_text())
        malformed = [{}, [], None, "pin", 1]
        malformed += [{key: value for key, value in pin.items() if key != field} for field in pin]
        malformed += [{**pin, field: value} for field in pin for value in (None, 1, [], {}, "", " ")]
        with tempfile.TemporaryDirectory() as scratch:
            work = Path(scratch)
            pin_path = work / "bad-pin.json"
            for index, value in enumerate(malformed):
                with self.subTest(pin=value):
                    pin_path.write_text(json.dumps(value))
                    output = work / str(index)
                    command = [sys.executable, str(Path(acceptance.__file__)), "--pin", str(pin_path), "--output", str(output)]
                    options = {"capture_output": True, "text": True, "env": {**acceptance.os.environ, "PATH": ""}}
                    result = subprocess.run(command, **options)
                    self.assertEqual(result.returncode, 1)
                    self.assertNotIn("Traceback", result.stderr)
                    before = {path.name: path.read_bytes() for path in output.iterdir()}
                    self.assertEqual(json.loads(before["receipt.json"]), {"schema_version": 1, "status": "failed", "errors": 1, "failure": "ValueError", "harness_failure": "ValueError"})
                    self.assertIn(b"| failed | 1 | ValueError |", before["summary.md"])
                    self.assertEqual(subprocess.run(command, **options).returncode, 1)
                    self.assertEqual({path.name: path.read_bytes() for path in output.iterdir()}, before)

    def test_setup_failure_preserves_evidence_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as scratch:
            work = Path(scratch)
            pin = work / "bad-pin.json"
            pin.write_text('{"sha256":"invalid"}')
            output = work / "evidence"
            command = [sys.executable, str(Path(acceptance.__file__)), "--pin", str(pin), "--output", str(output)]
            result = subprocess.run(command, capture_output=True)
            self.assertEqual(result.returncode, 1)
            before = {path.name: path.read_bytes() for path in output.iterdir()}
            self.assertEqual(json.loads(before["receipt.json"])["status"], "failed")
            self.assertTrue((output / "summary.md").exists())
            self.assertEqual(subprocess.run(command, capture_output=True).returncode, 1)
            self.assertEqual({path.name: path.read_bytes() for path in output.iterdir()}, before)

    def test_output_parent_preserves_separate_failed_runs(self):
        with tempfile.TemporaryDirectory() as scratch:
            work = Path(scratch)
            pin = work / "bad-pin.json"
            pin.write_text('{"sha256":"invalid"}')
            parent = work / "evidence"
            command = [sys.executable, str(Path(acceptance.__file__)), "--smoke", "--pin", str(pin), "--output-parent", str(parent)]
            before = {}
            for _ in range(2):
                result = subprocess.run(command, capture_output=True, text=True)
                self.assertEqual(result.returncode, 1)
                self.assertTrue(result.stdout.startswith("output: "))
                output = Path(result.stdout.strip()[len("output: "):])
                self.assertEqual(output.parent, parent)
                self.assertNotIn(output, before)
                self.assertEqual(json.loads((output / "receipt.json").read_text())["status"], "failed")
                self.assertIn("| failed | 1 | ValueError |", (output / "summary.md").read_text())
                before[output] = {path.name: path.read_bytes() for path in output.iterdir()}
            self.assertEqual(set(parent.iterdir()), set(before))
            for output, evidence in before.items():
                self.assertEqual({path.name: path.read_bytes() for path in output.iterdir()}, evidence)

    def test_output_options_are_mutually_exclusive(self):
        result = subprocess.run([sys.executable, str(Path(acceptance.__file__)), "--output", "unused", "--output-parent", "unused-parent"], capture_output=True)
        self.assertEqual(result.returncode, 2)

    def test_wrong_hash_is_never_executable(self):
        pin = json.loads((Path(__file__).parent / "release.json").read_text())
        with tempfile.TemporaryDirectory() as scratch:
            path = Path(scratch) / "server"
            def corrupt_download(*args, **kwargs):
                path.write_bytes(b"corrupted download")
            with patch.dict(acceptance.os.environ, {}, clear=True), patch.object(acceptance.subprocess, "run", side_effect=corrupt_download):
                with self.assertRaisesRegex(RuntimeError, "SHA-256 mismatch"):
                    acceptance.download(pin, path)
            self.assertFalse(path.stat().st_mode & 0o111)
            self.assertNotEqual(hashlib.sha256(path.read_bytes()).hexdigest(), pin["sha256"])


class FailureEvidenceTests(unittest.TestCase):
    def test_response_shape_errors_preserve_failure_evidence(self):
        secret = "tb_test_secret"

        def malformed_health_response(args):
            health = json.loads(response)
            return health["version"]

        def malformed_key_response(args):
            key = json.loads(response)
            return key["secret"].startswith("tb_")

        def secret_looking_missing_field(args):
            key = json.loads(response)
            return key[secret]

        def empty_cargo_artifacts(args):
            binaries = json.loads(response)
            return binaries[-1]

        cases = [(malformed_health_response, "{}", "KeyError"),
                 (malformed_health_response, "null", "TypeError"),
                 (malformed_health_response, json.dumps({"secret": secret}), "KeyError"),
                 (secret_looking_missing_field, "{}", "KeyError"),
                 (empty_cargo_artifacts, "[]", "IndexError")]
        cases += [(malformed_key_response, json.dumps({"secret": value}), "AttributeError")
                  for value in (None, 1, False, [], {})]
        for operation, response, failure in cases:
            for preexisting in (False, True):
                with self.subTest(operation=operation.__name__, response=response, preexisting=preexisting), tempfile.TemporaryDirectory() as scratch:
                    output = Path(scratch) / "evidence"
                    if preexisting:
                        output.mkdir()
                        (output / "receipt.json").write_text('{"status":"passed","errors":0}')
                        (output / "summary.md").write_text("previous driver metrics\n")
                    before = {path.name: path.read_bytes() for path in output.iterdir()} if preexisting else {}

                    with patch.object(acceptance, "run", side_effect=operation), patch("sys.stdout", new=io.StringIO()), patch("sys.stderr", new=io.StringIO()) as stderr:
                        with self.assertRaises(SystemExit) as exited:
                            acceptance.main(["--output", str(output)])
                    self.assertEqual(exited.exception.code, 1)
                    self.assertEqual(stderr.getvalue(), f"acceptance: {failure}\n")
                    self.assertNotIn(secret, stderr.getvalue())
                    after = {path.name: path.read_bytes() for path in output.iterdir()}
                    if preexisting:
                        self.assertEqual(after, before)
                    else:
                        self.assertEqual(json.loads(after["receipt.json"]),
                                         {"schema_version": 1, "status": "failed", "errors": 1,
                                          "failure": failure, "harness_failure": failure})
                        self.assertIn(f"| failed | 1 | {failure} |".encode(), after["summary.md"])
                        self.assertNotIn(secret.encode(), b"".join(after.values()))

    def test_environment_failure_marks_driver_evidence_failed(self):
        with tempfile.TemporaryDirectory() as scratch:
            output = Path(scratch) / "evidence"
            original = {"schema_version": 1, "status": "passed", "errors": 0,
                        "server_sha256": "a" * 64, "rows": [{"workload": "point_read", "throughput_ops_s": 123.0}]}
            summary = "| Workload | ops/s |\n| --- | ---: |\n| point_read | 123.0 |\n"

            def completed_driver_then_environment_failure(args):
                self.assertEqual(args.output, output)
                output.mkdir()
                acceptance.write_json(output / "receipt.json", original)
                (output / "summary.md").write_text(summary)
                raise OSError("environment.json write failed")

            with patch.object(acceptance, "run", side_effect=completed_driver_then_environment_failure), patch("sys.stdout"), patch("sys.stderr"):
                with self.assertRaises(SystemExit) as exited:
                    acceptance.main(["--output", str(output)])
            self.assertEqual(exited.exception.code, 1)
            self.assertEqual(json.loads((output / "receipt.json").read_text()),
                             {**original, "status": "failed", "errors": 1, "failure": "OSError", "harness_failure": "OSError"})
            failed_summary = (output / "summary.md").read_text()
            self.assertTrue(failed_summary.startswith("# C# acceptance failed\n"))
            self.assertIn("| failed | 1 | OSError |", failed_summary)
            self.assertTrue(failed_summary.endswith(summary))

    def test_failed_driver_counts_only_distinct_harness_failures(self):
        for error in (acceptance.DriverFailure(1, ["driver"]),
                      subprocess.CalledProcessError(1, ["driver"]),
                      OSError("environment.json write failed")):
            with self.subTest(error=type(error).__name__), tempfile.TemporaryDirectory() as scratch:
                output = Path(scratch) / "evidence"
                original = {"schema_version": 1, "status": "failed", "errors": 3,
                            "correctness_errors": 1, "rows": [{"workload": "point_read", "errors": 2}]}
                summary = "| Workload | Errors |\n| --- | ---: |\n| point_read | 2 |\n\nFinal readback errors: 1.\n"
                samples = '{"workload":"point_read","success":false}\n'

                def failed_driver(args):
                    self.assertEqual(args.output, output)
                    output.mkdir()
                    acceptance.write_json(output / "receipt.json", original)
                    (output / "summary.md").write_text(summary)
                    (output / "samples.jsonl").write_text(samples)
                    raise error

                with patch.object(acceptance, "run", side_effect=failed_driver), patch("sys.stdout"), patch("sys.stderr"):
                    with self.assertRaises(SystemExit) as exited:
                        acceptance.main(["--output", str(output)])
                self.assertEqual(exited.exception.code, 1)
                receipt = json.loads((output / "receipt.json").read_text())
                failed_summary = (output / "summary.md").read_text()
                if isinstance(error, acceptance.DriverFailure):
                    self.assertEqual(receipt, original)
                    self.assertNotIn("harness_failure", receipt)
                    self.assertIn("| failed | 3 | DriverFailure |", failed_summary)
                else:
                    self.assertEqual(receipt, {**original, "errors": 4,
                                              "failure": type(error).__name__, "harness_failure": type(error).__name__})
                    self.assertIn(f"| failed | 4 | {type(error).__name__} |", failed_summary)
                self.assertTrue(failed_summary.startswith("# C# acceptance failed\n"))
                self.assertTrue(failed_summary.endswith(summary))
                self.assertEqual((output / "samples.jsonl").read_text(), samples)

    def test_driver_exit_without_counted_failure_adds_error(self):
        cases = [(None, 1), ({"status": "passed", "errors": 0}, 1),
                 ({"status": "passed", "errors": 3}, 4),
                 ({"status": "failed", "errors": 0}, 1)]
        for original, total in cases:
            with self.subTest(receipt=original), tempfile.TemporaryDirectory() as scratch:
                output = Path(scratch) / "evidence"
                summary = "previous driver metrics\n"

                def driver_exit(args):
                    self.assertEqual(args.output, output)
                    if original is not None:
                        output.mkdir()
                        acceptance.write_json(output / "receipt.json", original)
                        (output / "summary.md").write_text(summary)
                    raise acceptance.DriverFailure(1, ["driver"])

                with patch.object(acceptance, "run", side_effect=driver_exit), patch("sys.stdout"), patch("sys.stderr"):
                    with self.assertRaises(SystemExit) as exited:
                        acceptance.main(["--output", str(output)])
                self.assertEqual(exited.exception.code, 1)
                self.assertEqual(json.loads((output / "receipt.json").read_text()),
                                 {**(original or {"schema_version": 1}), "status": "failed", "errors": total,
                                  "failure": "DriverFailure", "harness_failure": "DriverFailure"})
                failed_summary = (output / "summary.md").read_text()
                self.assertTrue(failed_summary.startswith("# C# acceptance failed\n"))
                self.assertIn(f"| failed | {total} | DriverFailure |", failed_summary)
                if original is not None:
                    self.assertTrue(failed_summary.endswith(summary))

    def test_failure_does_not_change_preexisting_driver_evidence(self):
        for error in (acceptance.DriverFailure(1, ["driver"]), subprocess.CalledProcessError(1, ["driver"]),
                      OSError("environment.json write failed")):
            with self.subTest(error=type(error).__name__), tempfile.TemporaryDirectory() as scratch:
                output = Path(scratch)
                acceptance.write_json(output / "receipt.json", {"status": "passed", "errors": 0, "rows": []})
                (output / "summary.md").write_text("previous driver metrics\n")
                before = {path.name: path.read_bytes() for path in output.iterdir()}
                with patch.object(acceptance, "run", side_effect=error), patch("sys.stdout"), patch("sys.stderr"):
                    with self.assertRaises(SystemExit) as exited:
                        acceptance.main(["--output", str(output)])
                self.assertEqual(exited.exception.code, 1)
                self.assertEqual({path.name: path.read_bytes() for path in output.iterdir()}, before)


class WorktreeTests(unittest.TestCase):
    def test_clean_unstaged_staged_and_untracked(self):
        with tempfile.TemporaryDirectory() as scratch:
            work = Path(scratch)
            def git(*args):
                return subprocess.run(["git", "-c", "user.name=Acceptance test", "-c", "user.email=acceptance@example.invalid", "-c", "commit.gpgsign=false", *args], cwd=work, capture_output=True, check=True)
            git("init", "-q")
            tracked = work / "tracked"
            tracked.write_text("initial\n")
            (work / ".gitignore").write_text("generated/\n")
            git("add", "tracked", ".gitignore")
            git("commit", "-qm", "initial")
            (work / "generated").mkdir()
            (work / "generated/receipt.json").write_text("{}")
            self.assertFalse(acceptance.benchmark_worktree_dirty(work))
            tracked.write_text("changed\n")
            self.assertTrue(acceptance.benchmark_worktree_dirty(work))
            git("add", "tracked")
            git("diff", "--quiet")  # Only the index differs from HEAD.
            self.assertTrue(acceptance.benchmark_worktree_dirty(work))
            git("commit", "-qm", "changed")
            self.assertFalse(acceptance.benchmark_worktree_dirty(work))
            source_state = acceptance.benchmark_source_state(work)
            self.assertEqual(source_state, {"benchmark_worktree_dirty": False,
                                           "benchmark_revision": git("rev-parse", "HEAD").stdout.decode().strip()})
            output = work / "bench/csharp-baseline/run"
            output.mkdir(parents=True)
            (output / "receipt.json").write_text('{"status":"passed"}')
            self.assertTrue(acceptance.benchmark_worktree_dirty(work))
            self.assertTrue(acceptance.benchmark_source_state(work)["benchmark_worktree_dirty"])
            self.assertFalse(source_state["benchmark_worktree_dirty"])

    def test_source_snapshot_precedes_output_parent_and_build(self):
        with tempfile.TemporaryDirectory() as scratch:
            args = acceptance.argparse.Namespace(pin=Path(acceptance.__file__).with_name("release.json"),
                                                output=Path(scratch) / "bench/baseline/run", smoke=True)

            def snapshot():
                self.assertFalse(args.output.parent.exists())
                return {"benchmark_worktree_dirty": False, "benchmark_revision": "a" * 40}

            def stop_at_build(*command_args, **kwargs):
                captured.assert_called_once_with()
                self.assertTrue(args.output.parent.is_dir())
                raise RuntimeError("stop before build")

            with patch.object(acceptance, "benchmark_source_state", side_effect=snapshot) as captured, patch.object(acceptance.subprocess, "run", side_effect=stop_at_build) as build:
                with self.assertRaisesRegex(RuntimeError, "stop before build"):
                    acceptance.run(args)
            build.assert_called_once_with(["make", "-C", str(acceptance.ROOT / "https-document"), "fmt", "lint", "test"], check=True)


if __name__ == "__main__":
    unittest.main()
