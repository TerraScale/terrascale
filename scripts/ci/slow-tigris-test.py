#!/usr/bin/env python3
"""Check private credential transport and scratch-only cleanup."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import stat
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("slow_tigris", Path(__file__).with_name("slow-tigris.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
APP = "ts-ci-slow-123-1"
BUCKET = f"terrascale-{APP}"


class SlowTigrisTests(unittest.TestCase):
    def invoke(self, action, filename, provider, app=APP):
        with patch.object(module.sys, "argv", ["slow-tigris.py", action, app, str(filename)]), \
                patch.object(module, "fly", side_effect=provider), contextlib.redirect_stdout(io.StringIO()) as output:
            module.main()
        return output.getvalue()

    def test_create_keeps_credentials_private_and_shell_quotes_values(self):
        with tempfile.TemporaryDirectory() as directory:
            filename = Path(directory) / "credentials"
            values = {key: "fixture ' secret" for key in module.VARIABLES}
            values["BUCKET_NAME"] = BUCKET
            output = self.invoke("create", filename, ["", "\n".join(f"{key}: {value}" for key, value in values.items())])
            self.assertNotIn("fixture", output)
            self.assertEqual(stat.S_IMODE(filename.stat().st_mode), 0o600)
            result = subprocess.run(["bash", "-c", 'set -a; . "$1"; python3 -c \'import os,json; print(json.dumps({k:os.environ[k] for k in os.environ if k.startswith("AWS_") or k=="BUCKET_NAME"}))\'', "fixture", str(filename)], capture_output=True, text=True, check=True)
            self.assertEqual(json.loads(result.stdout), values)

    def test_incomplete_credentials_fail_without_creating_a_file(self):
        with tempfile.TemporaryDirectory() as directory:
            filename = Path(directory) / "credentials"
            with self.assertRaisesRegex(RuntimeError, "five expected credentials"):
                self.invoke("create", filename, ["", "AWS_SECRET_ACCESS_KEY: fixture-private"])
            self.assertFalse(filename.exists())

    def test_destroy_removes_bucket_before_app_and_private_file(self):
        calls = []
        def provider(*args):
            calls.append(args)
            if args[:2] == ("apps", "list"):
                return json.dumps([{"Name": APP}])
            if args[:2] == ("machines", "list"):
                return "[]"
            if args[:2] == ("storage", "list"):
                return f" NAME │ ORG\n {BUCKET} │ terrascale-previews-515\n"
            return ""
        with tempfile.TemporaryDirectory() as directory:
            filename = Path(directory) / "credentials"
            filename.write_text("fixture-private")
            self.invoke("destroy", filename, provider)
            self.assertFalse(filename.exists())
        self.assertEqual(calls[-2:], [("storage", "destroy", BUCKET, "--yes"), ("apps", "destroy", APP, "--yes")])

    def test_partial_create_cleanup_and_absent_app_are_safe(self):
        with tempfile.TemporaryDirectory() as directory:
            filename = Path(directory) / "credentials"
            self.invoke("destroy", filename, ["[]"])
            self.invoke("destroy", filename, [json.dumps([{"Name": APP}]), "[]", " NAME │ ORG\n", ""])

    def test_cleanup_refuses_machines_and_unrelated_names(self):
        with tempfile.TemporaryDirectory() as directory:
            filename = Path(directory) / "credentials"
            with self.assertRaisesRegex(RuntimeError, "unexpectedly has machines"):
                self.invoke("destroy", filename, [json.dumps([{"Name": APP}]), '[{"id":"machine"}]'])
            for app in ("ts-auth", "ts-pr-123", "ts-ci-slow-123-1-other"):
                with self.assertRaises(ValueError):
                    self.invoke("destroy", filename, [], app)

    def test_provider_failure_never_prints_credentials(self):
        result = subprocess.CompletedProcess([], 1, "AWS_SECRET_ACCESS_KEY: fixture-private", "fixture-private")
        with patch.object(module.subprocess, "run", return_value=result):
            with self.assertRaises(RuntimeError) as error:
                module.fly("storage", "create")
        self.assertNotIn("fixture-private", str(error.exception))


if __name__ == "__main__":
    unittest.main()
