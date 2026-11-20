import fnmatch
import json
import os
import re
import shutil
import string
import subprocess
import sys
from pathlib import Path
from urllib.parse import urlencode
from urllib.request import urlopen

REVIEW = Path(os.environ["RUNNER_TEMP"]) / "review"
HIGH_SEVERITIES = {"critical", "high"}
MARKDOWN_ESCAPES = str.maketrans({char: f"&#{ord(char)};" for char in string.punctuation})
TEST_PATH = re.compile(
    r"(^|/)(tests?|testing|e2e|__tests__|__mocks__|(?-i:[^/]*Tests[^/]*))(/|$)"
    r"|\.(test[^/]*|e2e)\.(cs|fs|vb)proj$|\.(spec|test)\.[^/]+$|_tests?\.[^/]+$",
    re.IGNORECASE,
)
EDITABLE_PATH = re.compile(r"[\w. /-]+")
CONTAINER_IMAGE = "docker.io/library/node:24-bookworm-slim@sha256:dc26ae92c9fb15cf6b1d63e20f0944b2efe0f115b20e00be48fb23fceb061c54"



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
    if TEST_PATH.search(path) or not EDITABLE_PATH.fullmatch(path):
        return True
    return any(matches(path, pattern.strip()) for pattern in os.environ["DENY_PATHS"].splitlines() if pattern.strip())


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


def finding_paths():
    findings = json.loads((REVIEW / os.environ["FIXABLE_FILE"]).read_text())
    return {finding["path"] for finding in findings}


def guard():
    paths = finding_paths()
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
    if lines > int(os.environ["MAX_FIX_LINES"]):
        sys.exit("Patch exceeds the changed-line limit")


def output(name, value):
    with Path(os.environ["GITHUB_OUTPUT"]).open("a") as stream:
        stream.write(f"{name}={value}\n")


def fetch_fix(number, sha):
    if len(sha) != 40 or not all(char in string.hexdigits for char in sha):
        sys.exit("Invalid fix PR head SHA")
    git("fetch", "--no-tags", "origin", f"refs/pull/{number}/head")
    if git("rev-parse", "FETCH_HEAD").decode().strip() != sha:
        sys.exit("Fix PR head changed during preparation")


def apply_patch(patch):
    subprocess.run(["git", "apply", "--index", "-"], input=patch, check=True)


def plan():
    repository = os.environ["GITHUB_REPOSITORY"]
    head = os.environ["HEAD_SHA"]
    branch = f"ai-fix/{head[:12]}"
    main = git("rev-parse", "origin/main").decode().strip()
    if not is_ancestor(head, main):
        sys.exit("The reviewed head is outside main")
    output("branch", branch)
    output("base", main)
    query = urlencode({"head": f'{os.environ["GITHUB_REPOSITORY_OWNER"]}:{branch}', "state": "all"})
    with urlopen(f'{os.environ["GITHUB_API_URL"]}/repos/{repository}/pulls?{query}', timeout=15) as response:
        pulls = json.load(response)
    candidates = [
        pull for pull in pulls if pull["base"]["ref"] == "main"
        and (pull["head"]["repo"] or {}).get("full_name", "").casefold() == repository.casefold()
    ]
    opened = [pull for pull in candidates if pull["state"] == "open"]
    if len(opened) > 1:
        sys.exit("Multiple open PRs use the same AI fix branch")
    pull = opened[0] if opened else next((pr for pr in candidates if pr.get("merged_at")), None)
    if pull is None:
        return
    if pull["user"]["type"] != "Bot":
        sys.exit("The existing PR is outside the AI fix workflow")
    number, sha = int(pull["number"]), pull["head"]["sha"]
    already_merged = bool(pull.get("merged_at"))
    if not already_merged:
        output("number", number)
        output("sha", sha)
    fetch_fix(number, sha)
    parents = git("rev-list", "--parents", "-n1", sha).decode().split()[1:]
    if len(parents) != 1 or not is_ancestor(head, parents[0]) or not is_ancestor(parents[0], main):
        return
    if not already_merged:
        merge = subprocess.run(["git", "merge-tree", "--write-tree", main, sha], stdout=subprocess.DEVNULL, check=False)
        if merge.returncode == 1:
            return
        merge.check_returncode()

    base = parents[0]
    original = git("rev-parse", "HEAD").decode().strip()
    git("checkout", "--detach", base)
    try:
        apply_patch(git("diff", "--binary", "--no-ext-diff", "--no-textconv", base, sha))
        guard()
    except SystemExit as error:
        print(f"The existing fix no longer passes validation: {error}")
    else:
        if already_merged:
            output("number", number)
            output("sha", sha)
        output("base", base)
        output("resume", "true")
    finally:
        git("reset", "--hard", base)
        git("checkout", "--detach", original)


def is_ancestor(ancestor, head):
    status = subprocess.run(["git", "merge-base", "--is-ancestor", ancestor, head], check=False)
    if status.returncode == 1:
        return False
    status.check_returncode()
    return True


def generate():
    git("checkout", "--detach", os.environ["FIX_BASE"])
    temp = Path(os.environ["RUNNER_TEMP"])
    snapshot = temp / "snapshot"
    paths = sorted(finding_paths())
    tracked = set(tracked_files())
    if not paths or any(denied(path) or path not in tracked for path in paths):
        sys.exit("Review contains an invalid editable path")
    shutil.copytree(Path.cwd(), snapshot, symlinks=True, ignore=shutil.ignore_patterns(".git"))
    for path in snapshot.rglob("*"):
        if path.is_symlink():
            path.unlink()
    for path in paths:
        original = snapshot / path
        if not original.is_file():
            sys.exit("An editable file is missing from the snapshot")

    config = {
        "provider": {"cliproxy": {
            "npm": "@ai-sdk/openai-compatible",
            "options": {"baseURL": "{env:OPENCODE_PROXY_BASE}", "apiKey": "{env:OCR_LLM_TOKEN}"},
            "models": {"glm-5.3": {"name": "GLM-5.3"}},
        }},
        "lsp": False,
        "formatter": False,
        "permission": {
            "*": "deny", "glob": "allow", "grep": "allow", "list": "allow",
            "read": {"*": "deny", "repo": "allow", "repo/**": "allow", "findings.json": "allow"},
            "edit": {"*": "deny", **dict.fromkeys((f"repo/{path}" for path in paths), "allow")},
        },
    }
    config_path = temp / "opencode.json"
    config_path.write_text(json.dumps(config))
    binary = temp / "opencode/node_modules/opencode-linux-x64/bin/opencode"
    findings = REVIEW / os.environ["FIXABLE_FILE"]
    base_url = os.environ["OCR_LLM_URL"].rstrip("/").removesuffix("/chat/completions")
    environment = {"PATH": os.environ["PATH"], "HOME": os.environ["HOME"],
                   "OPENCODE_PROXY_BASE": base_url, "OCR_LLM_TOKEN": os.environ["OCR_LLM_TOKEN"]}
    docker = [
        "docker", "run", "--rm", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges", "--user", f"{os.getuid()}:{os.getgid()}",
        "--tmpfs", "/tmp:rw,nosuid,nodev,mode=1777", "--workdir", "/repo",
        "--env", "HOME=/tmp", "--env", "OPENCODE_CONFIG=/opencode.json",
        "--env", "OPENCODE_DISABLE_PROJECT_CONFIG=true", "--env", "OPENCODE_DISABLE_AUTOUPDATE=true",
        "--env", "OPENCODE_DISABLE_MODELS_FETCH=true", "--env", "OPENCODE_PROXY_BASE", "--env", "OCR_LLM_TOKEN",
        "--mount", f"type=bind,src={binary},dst=/opencode,readonly",
        "--mount", f"type=bind,src={config_path},dst=/opencode.json,readonly",
        "--mount", f"type=bind,src={findings},dst=/findings.json,readonly",
        "--mount", f"type=bind,src={snapshot},dst=/repo,readonly",
    ]
    for path in paths:
        docker.extend(["--mount", f"type=bind,src={snapshot / path},dst=/repo/{path}"])
    prompt = Path(__file__).with_name("fix-prompt.md").read_text()
    subprocess.run(
        [*docker, "--entrypoint", "/opencode", CONTAINER_IMAGE, "run", "--pure",
         "--model", "cliproxy/glm-5.3", "--file", "/findings.json", "--", prompt],
        env=environment, check=True,
    )
    for path in paths:
        shutil.copyfile(snapshot / path, path)
    (temp / "fix.patch").write_bytes(git("diff", "--binary", "--no-ext-diff", "--no-textconv"))


def validate():
    if os.environ["RESUME"] == "true":
        base = os.environ["RESUME_BASE"]
        git("checkout", "--detach", base)
        head = os.environ["EXISTING_SHA"]
        fetch_fix(int(os.environ["EXISTING_PR"]), head)
        patch = git("diff", "--binary", "--no-ext-diff", "--no-textconv", base, head)
    else:
        artifact = Path(os.environ["RUNNER_TEMP"]) / "fix.patch"
        if not artifact.exists():
            sys.exit("The existing fix changed after generation. Rerun all jobs to generate a replacement.")
        patch = artifact.read_bytes()
    if not patch:
        sys.exit("Patch is empty")
    apply_patch(patch)
    guard()


def checkpoint():
    ref = "refs/ai-review/last"
    git(
        "-c", "credential.helper=", "-c", "credential.helper=!gh auth git-credential",
        "push", "origin", f'--force-with-lease={ref}:{os.environ["WATERMARK"]}', f'{os.environ["HEAD_SHA"]}:{ref}',
    )


if __name__ == "__main__":
    commands = {"route": route, "plan": plan, "generate": generate, "validate": validate,
                "guard": guard, "checkpoint": checkpoint}
    commands[sys.argv[1]]()
