#!/usr/bin/env python3
"""Check cache/event boundaries and exercise Docker's actual restore-input filter."""
from pathlib import Path
import re
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
workflow = (root / ".github/workflows/ci.yml").read_text()
dockerfile = (root / "Dockerfile").read_text()
writers = re.findall(r"^\s+cache-to: (.+)$", workflow, re.M)
assert writers == ["${{ github.event_name == 'push' && github.ref == 'refs/heads/main' && format('type=gha,mode=max,scope={0}', github.job) || '' }}"], writers
# setup-dotnet saves its NuGet cache after any successful job, pull requests included: no job may enable it.
assert "cache: true" not in workflow
assert not re.search(r"uses: actions/cache(?:/save)?@", workflow), "Explicit cache writers need a main-only gate"
assert "cancel-in-progress: ${{ github.event_name != 'merge_group' }}" in workflow.split("\npermissions:", 1)[0]
assert "no-cache-filters: test-${{ github.job }}" in workflow
assert "no-cache-filters: slow-${{ matrix.area }}-run" in workflow

inputs = dockerfile.split("FROM base AS restore-inputs\n", 1)[1].split("\nFROM base AS restored", 1)[0]
command = inputs.split("\nRUN ", 1)[1].split("\n\n", 1)[0].replace("\\\n", " ")
fixtures = {
    "TerraScale.slnx": "<Solution/>", "Makefile": "restore:\n\tdotnet restore\n",
    "global.json": "{}", "NuGet.config": "<configuration/>", "Directory.Build.props": "<Project/>",
    "dotnet-tools.json": "{}", "src/nested/server/app.csproj": "<Project/>",
    "src/nested/server/packages.lock.json": "{}", "src/nested/server/Program.cs": "// source",
    "src/nested/server/assets/style.css": "body{}", "docs/ci.md": "documentation",
    "src/vendor/driver/locks/Driver.packages.lock.json": "{}", "src/vendor/driver/Directory.Build.targets": "<Project/>",
}
expected = set(fixtures) - {"src/nested/server/Program.cs", "src/nested/server/assets/style.css", "docs/ci.md"}

def filtered(change=None):
    with tempfile.TemporaryDirectory(prefix="restore-filter-") as directory:
        scratch = Path(directory)
        source, manifests = scratch / "source", scratch / "manifests"
        for filename, contents in fixtures.items():
            path = source / filename
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(contents + ("\nchanged" if filename == change else ""))
        actual = command.replace("/restore-inputs", str(source)).replace("/restore-manifests", str(manifests))
        subprocess.run(["bash", "-e", "-c", actual], check=True, capture_output=True, text=True)
        result = {str(path.relative_to(manifests)): path.read_bytes() for path in manifests.rglob("*") if path.is_file()}
        assert set(result) == expected, set(result)
        return result

baseline = filtered()
for path in fixtures:
    assert (filtered(path) != baseline) == (path in expected), path
print(f"cache-policy-test: ok (main-only writers, superseded runs cancelled, {len(fixtures)} restore invalidation controls)")
