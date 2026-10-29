"""Report Slow failures with gh without exporting response bodies or credentials."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
REPORT = ROOT / "test-results/slow-failure.json"


def failures(document):
    # Source method names identify failures; parameterized test names and raw output may hold secrets.
    namespace = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    methods = {}
    for test in document.findall(".//t:UnitTest", namespace):
        method = test.find("t:TestMethod", namespace)
        if method is not None:
            identity = method.get("className", "").split(",")[0] + "." + method.get("name", "")
            if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.+`]*", identity):
                methods[test.get("id")] = identity
    return sorted({methods.get(result.get("testId"), "UnidentifiedSlowTest")
                   for result in document.findall(".//t:UnitTestResult", namespace)
                   if result.get("outcome") == "Failed"})


def prepare():
    names = []
    for path in (ROOT / "test-results/tunit").glob("*slow*.trx"):
        try:
            names.extend(failures(ET.parse(path).getroot()))
        except ET.ParseError:
            names.append("MalformedSlowResult")
    names = sorted(set(names)) or ["SlowWorkflowInfrastructure"]
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text(json.dumps({"failures": names}, indent=2) + "\n")
    return names


def gh(*arguments):
    return subprocess.check_output(["gh", *arguments], text=True)


def report():
    names = json.loads(REPORT.read_text())["failures"] if REPORT.exists() else ["SlowWorkflowInfrastructure"]
    if not names or not all(isinstance(name, str) and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.+`]*", name) for name in names):
        raise ValueError("Invalid redacted Slow failure identities")
    fingerprint = hashlib.sha256("\n".join(sorted(set(names))).encode()).hexdigest()[:16]
    title = f"Dashboard Slow failure [{fingerprint}]"
    repository = os.environ["GITHUB_REPOSITORY"]
    run = f'{os.environ["GITHUB_SERVER_URL"]}/{repository}/actions/runs/{os.environ["GITHUB_RUN_ID"]}'
    body = (f"Commit: `{os.environ['GITHUB_SHA']}`\n\n"
            f"Environment: GitHub-hosted ubuntu-24.04; pinned global.json/Node/Chromium.\n\n"
            "Reproduce: `make dashboard-build`, install the bundled Playwright Chromium, "
            "then `make dashboard-test-slow`.\n\n"
            "Expected: every selected Slow journey passes. Actual: the Slow workflow failed. "
            "An infrastructure entry means setup/runner failure or no usable test result.\n\n"
            "Redacted result log (method identities only; argument values, messages, stacks and response bodies withheld):\n\n"
            + "\n".join(f"- `{name}`: Failed" for name in names)
            + f"\n\n[CI run and private diagnostic artifacts]({run})\n\n"
            "Slow is non-blocking; this failure remains visible. Sensitive vulnerability details stay in the approved private channel.\n")
    issues = json.loads(gh("issue", "list", "--repo", repository, "--state", "open",
                           "--search", f'"{title}" in:title', "--json", "number,title"))
    existing = next((issue for issue in issues if issue["title"] == title), None)
    with tempfile.TemporaryDirectory(prefix="dashboard-slow-report-") as directory:
        path = Path(directory) / "body.md"
        path.write_text(body)
        if existing:
            print(gh("issue", "comment", str(existing["number"]), "--repo", repository, "--body-file", str(path)))
        else:
            print(gh("issue", "create", "--repo", repository, "--title", title, "--body-file", str(path)))


def self_check():
    sample = ET.fromstring('''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
      <TestDefinitions><UnitTest id="1"><TestMethod className="Example.JourneyTests, Suite" name="RefusesForeignProject"/></UnitTest>
      <UnitTest id="2"><TestMethod className="Example.JourneyTests" name="Success"/></UnitTest></TestDefinitions>
      <Results><UnitTestResult testId="1" outcome="Failed" testName="secret-parameter"><Output><ErrorInfo><Message>Bearer secret</Message></ErrorInfo></Output></UnitTestResult>
      <UnitTestResult testId="2" outcome="Passed"/></Results></TestRun>''')
    assert failures(sample) == ["Example.JourneyTests.RefusesForeignProject"]
    assert "secret" not in json.dumps(failures(sample))
    assert failures(ET.fromstring("<TestRun/>")) == []
    print("PASS: Slow reports omit parameter values and raw diagnostic output")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-check"]:
        self_check()
    elif sys.argv[1:] == ["--prepare"]:
        prepare()
    elif not sys.argv[1:]:
        report()
    else:
        raise SystemExit("usage: report-slow-failure.py [--prepare|--self-check]")
