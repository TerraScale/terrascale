import json
import tempfile
import unittest
from pathlib import Path

import render_history


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
    def test_filename_timestamp_wins_over_mtime(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "postgresql-read-100-1-0.json").write_text(json.dumps(complete("old")))
            (root / "postgresql-read-200-1-0.json").write_text(json.dumps(complete("new")))
            self.assertEqual(render_history.load(root, "postgresql", "read")["environment"]["observed_identity"], "new")

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
