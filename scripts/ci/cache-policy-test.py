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
assert writers == ["type=gha,mode=min,scope=ci-base"], writers
docker_writer = workflow.split("      - name: Cache shared Docker base layers\n", 1)[1].split("\n      #", 1)[0]
assert "github.event_name == 'push' && github.ref == 'refs/heads/main'" in docker_writer
assert "target: base" in docker_writer
assert "cache-from: &cache-from type=gha,scope=ci-base" in workflow
assert "mode=max" not in workflow, "Published binaries and source snapshots must not fill GHA"
# setup-dotnet saves its NuGet cache after any successful job, pull requests included: no job may enable it.
assert "cache: true" not in workflow
assert not re.search(r"uses: actions/cache@", workflow), "Use explicit restore/save to restrict writers"
assert workflow.count("uses: actions/cache/save@") == 1
nuget_writer = workflow.split("uses: actions/cache/save@", 1)[1].split("\n      - ", 1)[0]
assert "github.event_name == 'push' && github.ref == 'refs/heads/main'" in nuget_writer
assert "steps.nuget.outputs.cache-primary-key" in nuget_writer
assert "uses: actions/cache/restore@" in workflow
assert "DeterministicSourcePaths: false" in workflow, "CallerFilePath fixtures need their real source path"
assert "ContinuousIntegrationBuild: false" not in workflow
assert "cancel-in-progress: ${{ github.event_name == 'pull_request' }}" in workflow.split("\npermissions:", 1)[0]
assert 'bash scripts/ci/test-area.sh fast "$AREA"' in workflow
assert "no-cache-filters: test-${{ env.AREA }}" in workflow
assert "no-cache-filters: slow-${{ env.AREA }}-run" in workflow
assert "steps: *slow-steps" in workflow
assert "if: env.AREA != 'auth'" in workflow
assert "if: env.AREA == 'auth'" in workflow
assert "native-audit:" not in workflow, "Remove the temporary executor comparison"

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
assert "needs: [changes, native, libs, auth, terrabase-build, terrabase]" in workflow, "Native/Fast must gate required test; Slow must not delay it"
assert "AREA: ${{ github.job }}" not in workflow, "github.job is unavailable in job-level env"

# Cheap policy failures stop native downloads and every expensive area; Slow runs independently of publication.
changes = workflow.split("\n  changes:\n", 1)[1].split("\n  native:\n", 1)[0]
for check in ("path-filter-test.py", "slow-report-test.py", "cache-policy-test.py",
              "turso-native-pipeline-test.py", "storage-format-check-test.sh", "e2e-summary-test.sh",
              "storage-format-check.sh"):
    assert check in changes, f"{check} must run before builds"
for area in ("auth", "terrabase"):
    slow = re.split(r"\n  [a-z][a-z0-9-]*:\n", workflow.split(f"\n  slow-{area}-main:\n", 1)[1], maxsplit=1)[0]
    assert "needs: [changes, native]" in slow, "Slow must not wait for image publication or rollout"
    assert "cancel-in-progress: true" not in slow, "Main results must survive superseding pushes"
assert "restore-keys: ${{ runner.os }}-nuget-${{ env.TS_DOTNET_REV }}-" in workflow

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
print(f"cache-policy-test: ok (main-only package/base writers, fresh hybrid suites, {len(fixtures)} restore invalidation controls)")
