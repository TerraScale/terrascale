"""Runner preflight checks only; no substituted production SDK or service."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile
import hashlib

RUNNER = Path(__file__).resolve().parents[1] / "run.sh"


class PackageInputs(unittest.TestCase):
    def invoke(self, package, digest, run_id="regression", regression=False):
        env = {**os.environ, **{"TERRASCALE_" + name: "present" for name in
               ("API_URL", "AUTH_URL", "PROJECT_ID", "DATABASE_ID", "API_KEY")},
               "TERRASCALE_SDK_NUPKG": str(package), "TERRASCALE_SDK_NUPKG_SHA256": digest,
               "TERRASCALE_JOURNEY_RUN_ID": run_id}
        command = (["bash", str(RUNNER), "--regression"] if regression else
                   ["bash", str(RUNNER), "--phase", "seed"])
        return subprocess.run(command, env=env,
                              capture_output=True, text=True, timeout=5)

    def test_regression_mode_requires_only_the_documented_package_inputs(self):
        env = os.environ.copy()
        for name in ("TERRASCALE_SDK_NUPKG", "TERRASCALE_SDK_NUPKG_SHA256"):
            env.pop(name, None)
        result = subprocess.run(["bash", str(RUNNER), "--regression"], env=env,
                                capture_output=True, text=True, timeout=5)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("missing required input: TERRASCALE_SDK_NUPKG", result.stderr)

    def test_bad_path_hash_and_identity_fail_before_restore(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "-package with spaces.nupkg"
            self.assertIn("not a regular file", self.invoke(path, "0" * 64).stderr)
            with zipfile.ZipFile(path, "w") as package:
                package.writestr("wrong.nuspec", "<package><metadata><id>Other</id><version>0.1.0</version></metadata></package>")
            self.assertIn("SHA-256 mismatch", self.invoke(path, "0" * 64).stderr)
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            result = self.invoke(path, digest)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("unexpected package ID", result.stderr)
            self.assertNotIn("Determining projects", result.stdout)
            self.assertIn("invalid journey", self.invoke(path, digest, "../escape").stderr)
            regression = self.invoke(path, digest, regression=True)
            self.assertIn("unexpected package ID", regression.stderr)
            self.assertNotIn("missing required input: TERRASCALE_API_URL", regression.stderr)


if __name__ == "__main__":
    unittest.main()
