import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--repo", required=True)
    parser.add_argument("--mode", choices=["report", "fail"])
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    policy = Path(__file__).resolve().parent
    terms = json.loads((policy / "terms.json").read_text())
    rules = json.loads((policy / "allow-list.json").read_text())
    owners = json.loads((policy / "owners.json").read_text())
    terms = [(t, re.compile(t["pattern"])) for t in terms]
    rules = [(r, re.compile(r["path"]), re.compile(r["match"]) if "match" in r else None)
             for r in rules if r.get("repo", args.repo) == args.repo]
    mode = args.mode or (root / ".legacy-scan-mode").read_text().strip()
    if mode not in {"report", "fail"}:
        raise ValueError(".legacy-scan-mode must contain report or fail")
    hits = []
    paths = git(root, "ls-files", "-z").decode("utf-8").split("\0")
    for name in sorted(set(paths) - {""}):
        path = root / name
        data = os.readlink(path).encode("utf-8") if path.is_symlink() else path.read_bytes()
        if b"\0" in data:
            continue
        content = data.decode("utf-8", errors="replace")
        scoped = [(r, match) for r, pattern, match in rules if pattern.search(name)]
        for number, line in [(0, name), *enumerate(content.splitlines(), 1)]:
            for term, pattern in terms:
                allowed = [(r, m) for r, m in scoped
                           if term["id"] in r.get("terms", [term["id"]])]
                if any(m is None for _, m in allowed):
                    continue
                spans = [m.span() for _, allow in allowed for m in allow.finditer(line)]
                for match in pattern.finditer(line):
                    if any(start <= match.start() and match.end() <= end for start, end in spans):
                        continue
                    owner = owners.get(args.repo, "P1" if name.startswith(("src/TerraScale.Authentication", "tests/TerraScale.Authentication")) else "C3" if name.startswith("docs/") else term["owner"])
                    hits.append((name, number, term["id"], match.group(), owner))
    sha = git(root, "rev-parse", "HEAD").decode().strip()
    print(f"Legacy scan: {args.repo} {sha} mode={mode} hits={len(hits)}")
    for name, number, term, match, owner in hits:
        print(json.dumps(dict(path=name, line=number, term=term, match=match, owner=owner)))
    if args.output:
        def cell(value):
            return str(value).replace("&", "&amp;").replace("<", "&lt;").replace("|", "&#124;").replace("`", "&#96;").replace("\n", " ")

        report = [f"# Legacy hits: {args.repo}", "", f"Commit: `{sha}`", "",
                  f"Mode: `{mode}`. Total hits: **{len(hits)}**.", "",
                  "Line 0 denotes a tracked filename. Counts are term occurrences, including filename hits.", "",
                  "| Path | Line | Term | Matched text | Owner task |", "|---|---:|---|---|---|"]
        report.extend("| " + " | ".join(map(cell, hit)) + " |" for hit in hits)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text("\n".join(report) + "\n")
    return 1 if hits and mode == "fail" else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"Legacy scan error: {error}", file=sys.stderr)
        sys.exit(2)
