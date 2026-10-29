"""Lint the dashboard's TUnit-only E2E test boundary.

This source guard cannot prove that a journey crosses the running application;
runtime discovery and review of the exercised boundaries are also required.
"""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
suite = root / "tests/TerraScale.Dashboard.E2E"
project = ET.parse(suite / "TerraScale.Dashboard.E2E.csproj")
packages = {item.get("Include") for item in project.findall(".//PackageReference")}
assert "TUnit" in packages, "Dashboard E2E tests must use TUnit"
for project_file in [*(root / "src/TerraScale.Dashboard").rglob("*.csproj"), *suite.rglob("*.csproj")]:
    if {"bin", "obj", "vendor", "node_modules", "test-results", ".git"}.intersection(project_file.relative_to(root).parts):
        continue
    references = {item.get("Include", "").casefold()
                  for item in ET.parse(project_file).findall(".//PackageReference")}
    assert not references.intersection({"xunit", "xunit.v3", "nunit", "nunit3testadapter",
                                       "mstest", "mstest.testframework", "mstest.testadapter",
                                       "microsoft.net.test.sdk"}), \
        f"Do not introduce another test runner or isolated unit suite: {project_file}"
    if "tunit" in references:
        assert project_file == suite / "TerraScale.Dashboard.E2E.csproj", \
            f"TUnit must run the single application E2E suite: {project_file}"
app = root / "src/TerraScale.Dashboard"
assert not [path for path in app.rglob("*.cs") if not {"bin", "obj"}.intersection(path.relative_to(app).parts)
            and "TerraScale.Dashboard.E2E" in path.read_text()], "Test providers must stay out of the production application"
assert not [path for path in app.rglob("*.cs") if not {"bin", "obj"}.intersection(path.relative_to(app).parts)
            and "TerraScale.Dashboard.E2E.Demo" in path.read_text()], \
    "Synthetic providers must only be compiled into the E2E test executable"
assert not (app / "Demo").exists(), "Demo fixtures belong in tests/TerraScale.Dashboard.E2E/Demo"
assert not [item.get("Include") for item in ET.parse(app / "TerraScale.Dashboard.csproj").iter()
            if item.tag != "ProjectReference" and any(outside in (item.get("Include") or "") for outside in ("..", "$(RepositoryRoot)"))], \
    "The application must not include files from outside its project"

count = 0
for source in [*app.rglob("*.cs"), *suite.rglob("*.cs")]:
    if {"bin", "obj", "vendor", "node_modules", "test-results", ".git"}.intersection(source.relative_to(root).parts):
        continue
    text = source.read_text()
    attributes = "\n".join(re.findall(r"\[([^\]]+)\]", text))
    assert not re.search(r"\b(?:NotInParallel|ParallelLimiter)(?:Attribute)?\b", attributes), \
        f"Fix per-test isolation instead of limiting parallel execution: {source}"
    assert not re.search(r"\bSharedType\s*\.\s*PerTestSession\b", attributes), \
        f"Give each test its own fixture instead of sharing session state: {source}"
    assert not re.search(r"\b(?:Fact|Theory|TestMethod|DataTestMethod|TestCase|TestCaseSource)(?:Attribute)?\b", attributes), \
        f"Use TUnit application journeys, not another test framework: {source}"
    if not re.search(r"(?:^|[,\s:.])Test(?:Attribute)?\s*(?:\(|,|$)", attributes, re.MULTILINE):
        continue
    assert source.is_relative_to(suite), f"TUnit tests belong in the E2E suite: {source}"
    assert source.name.endswith(("JourneyTests.cs", "ContractTests.cs")), f"Create application journeys or HTTP contract fixtures: {source}"
    assert not re.search(r"GetRequiredService\s*<|GetService\s*<|BindingFlags\.|typeof\([^)]*\)\.GetMethod", text), \
        f"Exercise running application boundaries instead of invoking isolated services: {source}"
    count += len(re.findall(r"\bclass\s+\w+(?:Journey|Contract)Tests\b", text))
assert count > 0, "The E2E suite must discover application journeys"
for source in [*suite.rglob("*.py"), *(root / "scripts/dashboard").glob("test_*.py")]:
    assert not re.search(r"(?:from|import)\s+(?:unittest|pytest)\b", source.read_text()), \
        f"Use a TUnit process/HTTP E2E journey instead of a separate Python unit suite: {source}"
assert not (root / "scripts/dashboard/test_stripe_audit.py").exists(), \
    "The legacy isolated Stripe audit suite has been replaced by TUnit process/TLS-HTTP E2Es"
for retired in ("e2e-shard.sh", "e2e-shard-count.sh", "check-e2e-shards.sh"):
    assert not (root / "scripts/dashboard" / retired).exists(), \
        f"Retire the separate Go test-discovery runner: {retired}"
makefile = (root / "scripts/dashboard/Makefile").read_text()
assert "test-unit:" not in makefile and "python3 -m unittest" not in makefile, \
    "Dashboard test targets must not run isolated unit suites"
assert not re.search(r"\bgo\s+(?:test|build|run)\b|scripts/check-e2e-shards\.sh", makefile), \
    "TUnit must own performance and discovery targets as well as the main E2E suite"
print(f"PASS: TUnit application/HTTP contract boundary ({count} classes); synthetic providers confined to the E2E project")
