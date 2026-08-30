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
        "environment": {"observed_identity": identity},
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


if __name__ == "__main__":
    unittest.main()
