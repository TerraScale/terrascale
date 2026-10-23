#!/usr/bin/env python3
"""Check cache/event boundaries and exercise Docker's actual restore-input filter."""
from pathlib import Path
import json
import re
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
workflow = (root / ".github/workflows/ci.yml").read_text()
dockerfile = (root / "Dockerfile").read_text()
writers = re.findall(r"^\s+cache-to: (.+)$", workflow, re.M)
assert writers == ["${{ github.event_name == 'push' && github.ref == 'refs/heads/main' && format('type=gha,mode=max,scope={0}', env.AREA) || '' }}"], writers
# setup-dotnet saves its NuGet cache after any successful job, pull requests included: no job may enable it.
assert "cache: true" not in workflow
assert not re.search(r"uses: actions/cache(?:/save)?@", workflow), "Explicit cache writers need a main-only gate"
assert "cancel-in-progress: ${{ github.event_name == 'pull_request' }}" in workflow.split("\npermissions:", 1)[0]
assert "no-cache-filters: test-${{ env.AREA }}" in workflow
assert "no-cache-filters: slow-${{ env.AREA }}-run" in workflow

# Guard behavior at the build graph boundary: publishing and Slow must never rerun checked Fast stages.
stages = {
    name: (base, body) for base, name, body in re.findall(
        r"^FROM (\S+) AS ([a-z0-9-]+)\n(.*?)(?=^FROM |\Z)", dockerfile, re.M | re.S)
}
for stage in ("publish-terrabase", "publish-ts-auth", "slow-auth-build", "slow-terrabase-build"):
    base, body = stages[stage]
    assert base == "source", (stage, base)
    assert not any(check in body for check in ("fmt-check", "auth-check", "terrabase-check", "test-fast")), stage

# Area whitespace filters together retain every project in the full solution, including the excluded build-check.
all_projects = set(re.findall(r'<Project Path="([^"]+)"', (root / "TerraScale.slnx").read_text()))
formatted = set()
for area in ("libs", "auth", "terrabase"):
    formatted.update(json.loads((root / f"{area}.slnf").read_text())["solution"]["projects"])
    assert f"SOLUTION={area}.slnf" in stages[f"build-{area}"][1], area
build_check = "src/TerraBase.Analyzers.BuildCheck/TerraBase.Analyzers.BuildCheck.csproj"
assert f"dotnet format whitespace {build_check} --verify-no-changes --no-restore" in stages["build-terrabase"][1]
assert all_projects <= formatted | {build_check}, all_projects - formatted - {build_check}
assert "needs: [changes, libs, auth, terrabase]" in workflow, "Slow must not delay required test"
assert "AREA: ${{ github.job }}" not in workflow, "github.job is unavailable in job-level env"

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
