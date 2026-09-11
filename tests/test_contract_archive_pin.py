from pathlib import Path
import re
import unittest


class ContractArchivePinTests(unittest.TestCase):
    def test_matches_producer_handoff(self):
        pin = (Path(__file__).parents[1] / "CONTRACT_ARCHIVE_SHA256").read_text().strip()
        self.assertRegex(pin, r"[0-9a-f]{64}")
        self.assertEqual(pin, "b0b0297f3facf5c0ed9cc17b86a6cec477db4babad47ac9a62ce7a2e2134502b")
