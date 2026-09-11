from pathlib import Path
import re
import unittest


class ContractArchivePinTests(unittest.TestCase):
    def test_matches_producer_handoff(self):
        pin = (Path(__file__).parents[1] / "CONTRACT_ARCHIVE_SHA256").read_text().strip()
        self.assertRegex(pin, r"[0-9a-f]{64}")
        self.assertEqual(pin, "e7a8bcbe752d814006c52fd40df055d54ac9adb7e3c1e5050630951d8f3498af")
