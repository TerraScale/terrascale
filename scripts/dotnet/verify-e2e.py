"""Lint the dashboard's TUnit-only E2E test boundary.

This source guard cannot prove that a journey crosses the running application;
runtime discovery and review of the exercised boundaries are also required.
"""
from pathlib import Path
import json
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
suite = root / "tests/dotnet/e2e"
project = ET.parse(suite / "TerraScale.Dashboard.E2E.csproj")
packages = {item.get("Include") for item in project.findall(".//PackageReference")}
assert "TUnit" in packages, "Dashboard E2E tests must use TUnit"
browser_package = next(item.get("Version") for item in project.findall(".//PackageReference")
                       if item.get("Include") == "Microsoft.Playwright")
assert json.loads((root / "package.json").read_text())["devDependencies"]["playwright-core"] == browser_package, \
    "Go capture, native TUnit browsers and JavaScript journeys must use the same pinned Playwright version"
for project_file in root.rglob("*.csproj"):
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
app = ET.parse(root / "TerraScale.Dashboard.csproj")
assert any("tests/**/*.cs" in item.get("Remove", "") for item in app.findall(".//Compile")), \
    "Test providers must be excluded from the production application"
excludes = ";".join(item.text or "" for item in app.findall(".//DefaultItemExcludes"))
assert "tests/**" in excludes, "Test provider files and nested test build artifacts must be excluded from app content"

count = 0
for source in root.rglob("*.cs"):
    if {"bin", "obj", "vendor", "node_modules", "test-results", ".git"}.intersection(source.relative_to(root).parts):
        continue
    text = source.read_text()
    attributes = "\n".join(re.findall(r"\[([^\]]+)\]", text))
    assert not re.search(r"\b(?:Fact|Theory|TestMethod|DataTestMethod|TestCase|TestCaseSource)(?:Attribute)?\b", attributes), \
        f"Use TUnit application journeys, not another test framework: {source}"
    if not re.search(r"(?:^|[,\s:.])Test(?:Attribute)?\s*(?:\(|,|$)", attributes, re.MULTILINE):
        continue
    assert source.is_relative_to(suite), f"TUnit tests belong in the E2E suite: {source}"
    assert source.name.endswith("JourneyTests.cs"), f"Create application journeys: {source}"
    assert not re.search(r"GetRequiredService\s*<|GetService\s*<|BindingFlags\.|typeof\([^)]*\)\.GetMethod", text), \
        f"Exercise running application boundaries instead of invoking isolated services: {source}"
    count += len(re.findall(r"\bclass\s+\w+JourneyTests\b", text))
assert count > 0, "The E2E suite must discover application journeys"
for source in [*(root / "tests").rglob("*.py"), *(root / "scripts").glob("test_*.py")]:
    assert not re.search(r"(?:from|import)\s+(?:unittest|pytest)\b", source.read_text()), \
        f"Use a TUnit process/HTTP E2E journey instead of a separate Python unit suite: {source}"
assert not (root / "scripts/test_stripe_audit.py").exists(), \
    "The legacy isolated Stripe audit suite has been replaced by TUnit process/TLS-HTTP E2Es"
for retired in ("e2e-shard.sh", "e2e-shard-count.sh", "check-e2e-shards.sh"):
    assert not (root / "scripts" / retired).exists(), \
        f"Retire the separate Go test-discovery runner: {retired}"
makefile = (root / "Makefile").read_text()
assert "test-unit:" not in makefile and "python3 -m unittest" not in makefile, \
    "Dashboard test targets must not run isolated unit suites"
assert not re.search(r"\$\(GO\)\s+test\b|scripts/check-e2e-shards\.sh", makefile), \
    "TUnit must own performance and discovery targets as well as the main E2E suite"
assert "'/*/*/*/*[Category!=ReferenceCapture]'" in makefile, \
    "Normal E2E runs must not regenerate their visual reference"
reference_target = makefile.split("dotnet-parity-reference:", 1)[1].split("\n\n", 1)[0]
assert "GoReferenceCaptureJourneyTests" in reference_target and "$(GO) test" not in reference_target, \
    "TUnit must own original Go browser capture and host lifecycle"
reference_test = (suite / "GoReferenceCaptureJourneyTests.cs").read_text()
native_raster = re.search(r'Args\s*=\s*\[([^\]]+)\]', reference_test)
browser_comparison = (root / "tests/dotnet/parity.mjs").read_text()
browser_raster = re.search(r'args:\s*\[([^\]]+)\]', browser_comparison)
assert native_raster and browser_raster, "Both strict capture harnesses must declare their fixed raster environment"
assert re.findall(r'"([^\"]+)"', native_raster.group(1)) == re.findall(r"'([^']+)'", browser_raster.group(1)), \
    "Original Go capture and C# comparison must use identical Chromium raster arguments"
native_freeze = next(line.strip() for line in reference_test.splitlines() if "const freeze = async" in line)
browser_freeze = next(line.strip() for line in browser_comparison.splitlines() if "const freeze = async" in line)
assert native_freeze == browser_freeze, "Both hosts must settle the same animation pause before capture"
frame_wait = "await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));"
settled_frames = f"await freeze(); {frame_wait} await freeze(); {frame_wait}"
for harness in (reference_test, browser_comparison):
    assert settled_frames in re.sub(r"\s+", " ", harness), \
        "Both capture harnesses must commit frames after the final animation freeze"
assert '[Explicit]' in reference_test and '[Category("ReferenceCapture")]' in reference_test, \
    "Only explicit capture can replace Go reference artifacts"
reference_host = (root / "tests/dotnet/go-reference/main.go").read_text()
assert not re.search(r'"testing"|func\s+Test\w*\s*\(', reference_host), \
    "The reference host must be a plain executable, not a second test runner"
print(f"PASS: TUnit E2E boundary ({count} journey classes); demo sources excluded from production")
