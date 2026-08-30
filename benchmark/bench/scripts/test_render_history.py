import json
import importlib.util
import tempfile
import unittest
from pathlib import Path

_MODULE_SPEC = importlib.util.spec_from_file_location(
    "render_history", Path(__file__).with_name("render-history.py")
)
render_history = importlib.util.module_from_spec(_MODULE_SPEC)
_MODULE_SPEC.loader.exec_module(render_history)
FIXTURE = json.loads(
    Path(__file__).with_name("retained-attempt-selection-fixture.json").read_text()
)


def complete(identity="new"):
    return {
        "status": "complete",
        "target": "postgresql",
        "workload": "read",
        "config": {
            "seed_documents": 1,
            "operations": 1,
            "samples": 1,
            "concurrency": 1,
            "warmup_operations": 0,
            "noise_percent": 5.0,
        },
        "environment": {
            "observed_identity": identity,
            "endpoint": "postgres://127.0.0.1/bench",
            "client_transport": "postgres TCP",
            "unix_seconds": 1,
            "kernel": "Linux test",
            "logical_cpus": 1,
        },
        "samples": [
            {
                "operations": 1,
                "wall_seconds": 1.0,
                "throughput_ops_per_s": 1.0,
                "p50_us": 1.0,
                "p95_us": 1.0,
                "p99_us": 1.0,
            }
        ],
    }


class RetainedAttemptTests(unittest.TestCase):
    def test_markdown_history_is_immutable_and_stays_inside_its_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first = render_history.write_retained(root, "first\n")
            second = render_history.write_retained(root, "second\n")

            self.assertEqual(first.parent, root)
            self.assertEqual(second.parent, root)
            self.assertNotEqual(first, second)
            self.assertEqual(first.read_text(), "first\n")
            self.assertEqual(second.read_text(), "second\n")

    def test_filename_timestamp_wins_over_mtime(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / FIXTURE["attempts"][0]).write_text(json.dumps(complete("old")))
            (root / FIXTURE["attempts"][1]).write_text(json.dumps(complete("new")))
            self.assertEqual(
                max(
                    (path.name for path in root.glob("postgresql-read-*.json")),
                    key=lambda name: render_history._attempt_key(Path(name), "postgresql", "read"),
                ),
                FIXTURE["newest"],
            )
            self.assertEqual(
                render_history.load(root, FIXTURE["target"], FIXTURE["workload"])["environment"]["observed_identity"],
                "new",
            )

    def test_failed_newest_attempt_blocks_older_success(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "postgresql-read-100-1-0.json").write_text(json.dumps(complete("old")))
            (root / "postgresql-read-200-1-0.json").write_text(json.dumps({"status": "failed", "error": "offline"}))
            document = render_history.load(root, "postgresql", "read")
            self.assertEqual(document["status"], "failed")

    def test_malformed_newest_attempt_blocks_older_success(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "postgresql-read-100-1-0.json").write_text(json.dumps(complete("old")))
            (root / "postgresql-read-200-1-0.json").write_text("{broken")
            document = render_history.load(root, "postgresql", "read")
            self.assertEqual(document["status"], "invalid")

    def test_legacy_is_used_only_when_no_attempt_exists(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "postgresql-read.json").write_text(json.dumps(complete("legacy")))
            self.assertEqual(render_history.load(root, "postgresql", "read")["environment"]["observed_identity"], "legacy")
            (root / "postgresql-read-100-1-0.json").write_text(json.dumps(complete("attempt")))
            self.assertEqual(render_history.load(root, "postgresql", "read")["environment"]["observed_identity"], "attempt")

    def test_empty_samples_are_invalid_even_when_status_is_complete(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            document = complete()
            document["samples"] = []
            (root / "postgresql-read-100-1-0.json").write_text(json.dumps(document))
            self.assertEqual(render_history.load(root, "postgresql", "read")["status"], "invalid")

    def test_in_process_boundary_is_invalid(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            document = complete()
            document["environment"]["client_transport"] = "in-process engine"
            (root / "postgresql-read-100-1-0.json").write_text(json.dumps(document))
            self.assertEqual(render_history.load(root, "postgresql", "read")["status"], "invalid")

    def test_throughput_mismatch_is_invalid(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            document = complete()
            document["samples"][0]["throughput_ops_per_s"] = 2.0
            (root / "postgresql-read-100-1-0.json").write_text(json.dumps(document))
            self.assertEqual(render_history.load(root, "postgresql", "read")["status"], "invalid")


if __name__ == "__main__":
    unittest.main()
