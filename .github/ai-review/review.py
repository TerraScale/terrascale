import fnmatch
import json
import os
import re
import string
import subprocess
import sys
from pathlib import Path

REVIEW = Path(os.environ["RUNNER_TEMP"]) / "review"
HIGH_SEVERITIES = {"critical", "high"}
MARKDOWN_ESCAPES = str.maketrans({char: f"&#{ord(char)};" for char in string.punctuation})
TEST_PROJECT = re.compile(
    r"(^|/)(tests?|testing|__tests__|__mocks__|[^/]*-tests)(/|$)"
    r"|\.(tests?[^/]*|e2e)(/|\.(cs|fs|vb)proj$)",
    re.IGNORECASE,
)


def git(*args):
    return subprocess.check_output(["git", *args])


def matches(path, pattern):
    parts = path.split("/")

    def match(index, patterns):
        if not patterns:
            return index == len(parts)
        first, *rest = patterns
        if first == "**":
            return any(match(end, rest) for end in range(index, len(parts) + 1))
        return index < len(parts) and fnmatch.fnmatchcase(parts[index], first) and match(index + 1, rest)

    return match(0, pattern.split("/"))


def denied(path):
    patterns = os.environ["DENY_PATHS"].splitlines()
    return any(matches(path, pattern.strip()) for pattern in ["e2e/**", *patterns] if pattern.strip())


def tracked_files():
    for record in git("ls-tree", "-rz", "HEAD").split(b"\0"):
        if record.startswith((b"100644 ", b"100755 ")):
            yield os.fsdecode(record.split(b"\t", 1)[1])


def cell(value):
    return " ".join(str(value).split()).translate(MARKDOWN_ESCAPES)


def table(findings, head):
    rows = ["| File and line | Severity | Finding | Head | Disposition |", "| --- | --- | --- | --- | --- |"]
    for finding in findings:
        location = f'{finding["path"]}:{finding["start_line"]}'
        rows.append(
            f'| {cell(location)} | {cell(finding["severity"])} | {cell(finding["content"])} |'
            f' `{head}` | {finding["disposition"]} |'
        )
    return "\n".join(rows) + "\n"


def route():
    result = json.loads((REVIEW / "result.json").read_text())
    comments = result["comments"] or []
    head = os.environ["HEAD_SHA"]
    files = set(tracked_files())
    rows, kept, fixable = [], [], []
    for finding in comments:
        path = finding["path"]
        if finding.get("severity") not in HIGH_SEVERITIES:
            rows.append({**finding, "severity": finding.get("severity", ""), "disposition": "summary only"})
            continue
        can_fix = path in files and not denied(path)
        row = {**finding, "disposition": "auto-fix" if can_fix else "report only"}
        rows.append(row)
        kept.append(row)
        if can_fix:
            fixable.append(row)

    run = f'{os.environ["GITHUB_SERVER_URL"]}/{os.environ["GITHUB_REPOSITORY"]}/actions/runs/{os.environ["GITHUB_RUN_ID"]}'
    prefix = f'Review of `{head}` · [Workflow run]({run})\n\n'
    filename = "fixable.json"
    (REVIEW / filename).write_text(json.dumps(fixable, indent=2) + "\n")
    (REVIEW / "report.md").write_text(prefix + table(kept, head))
    (REVIEW / "fix.md").write_text(prefix + table(fixable, head))
    with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a") as summary:
        summary.write(prefix + (table(rows, head) if rows else "No findings.\n"))
    with Path(os.environ["GITHUB_OUTPUT"]).open("a") as output:
        output.write(f"fixable_count={len(fixable)}\nfixable_file={filename}\n")
        output.write(f"report={str(bool(kept)).lower()}\n")
        complete = result["status"] in {"complete", "skipped", "success"}
        output.write(f"checkpoint={str(complete).lower()}\n")


def guard():
    findings = json.loads((REVIEW / os.environ["FIXABLE_FILE"]).read_text())
    paths = {finding["path"] for finding in findings}
    if git("ls-files", "-z", "--others", "--ignored", "--exclude-standard"):
        sys.exit("Patch created ignored files")
    git("add", "-A")
    changes = git(
        "diff", "--cached", "--numstat", "--no-renames", "--no-ext-diff", "--no-textconv", "-z", "HEAD"
    ).split(b"\0")[:-1]
    if not changes:
        sys.exit("Patch is empty")
    lines = 0
    for change in changes:
        inserted, deleted, name = change.split(b"\t", 2)
        path = os.fsdecode(name)
        if denied(path) or path not in paths:
            sys.exit("Patch changes a path outside the permitted findings")
        if inserted == b"-" or deleted == b"-":
            sys.exit("Patch changes a binary file")
        lines += int(inserted) + int(deleted)
    for change in git("diff", "--cached", "--raw", "--no-renames", "-z", "HEAD").split(b"\0")[:-1:2]:
        old_mode, new_mode = change[1:].split()[:2]
        if old_mode != new_mode and new_mode != b"000000" and old_mode != b"000000":
            sys.exit("Patch changes a file mode")
        if new_mode not in {b"000000", b"100644", b"100755"}:
            sys.exit("Patch adds a non-regular file")
    added = git("diff", "--cached", "--name-only", "--no-renames", "--diff-filter=A", "-z", "HEAD")
    if any(TEST_PROJECT.search(path) for path in os.fsdecode(added).split("\0")):
        sys.exit("Patch adds a test project or file")
    if lines > int(os.environ["MAX_FIX_LINES"]):
        sys.exit("Patch exceeds the changed-line limit")


def checkpoint():
    ref = "refs/ai-review/last"
    git(
        "-c", "credential.helper=", "-c", "credential.helper=!gh auth git-credential",
        "push", "origin", f'--force-with-lease={ref}:{os.environ["WATERMARK"]}', f'{os.environ["HEAD_SHA"]}:{ref}',
    )


if __name__ == "__main__":
    {"route": route, "guard": guard, "checkpoint": checkpoint}[sys.argv[1]]()
