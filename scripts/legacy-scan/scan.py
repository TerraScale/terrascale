import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args])


def load_allow_list(policy):
    files = sorted((policy / "allow-list").glob("*.json"))
    if not files:
        raise ValueError(f"no allow-list files in {policy / 'allow-list'}")
    rules = []
    seen = set()
    for file in files:
        entries = json.loads(file.read_text())
        if not isinstance(entries, list):
            raise ValueError(f"{file.name} must contain a JSON array of allow-list entries")
        paths = [entry["path"] for entry in entries]
        if paths != sorted(paths):
            raise ValueError(f"{file.name} entries must be sorted by path")
        for entry in entries:
            key = json.dumps(entry, sort_keys=True)
            if key in seen:
                raise ValueError(f"duplicate allow-list entry in {file.name}: {entry['path']}")
            seen.add(key)
        rules.extend(entries)
    return rules


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--repo", required=True)
    parser.add_argument("--mode", choices=["report", "fail"])
    parser.add_argument("--output", type=Path)
    parser.add_argument("--path-prefix", help="Scan only paths under this repository-relative directory")
    parser.add_argument("--term", action="append", choices=["documents", "collections", "document-feeds", "lattices"], help="Scan only these retired document API terms (repeatable)")
    parser.add_argument("--changed-from", help="Scan only existing files changed since this Git revision")
    args = parser.parse_args()
    root = args.root.resolve()
    policy = Path(__file__).resolve().parent
    terms = json.loads((policy / "terms.json").read_text())
    rules = load_allow_list(policy)
    owners = json.loads((policy / "owners.json").read_text())
    terms = [(t, re.compile(t["pattern"])) for t in terms if not args.term or t["id"] in args.term]
    rules = [(r, re.compile(r["path"]), re.compile(r["match"]) if "match" in r else None)
             for r in rules if r.get("repo", args.repo) == args.repo]
    mode = args.mode or (root / ".legacy-scan-mode").read_text().strip()
    if mode not in {"report", "fail"}:
        raise ValueError(".legacy-scan-mode must contain report or fail")
    hits = []
    paths = (git(root, "diff", "--name-only", "--diff-filter=ACMRT", "-z", args.changed_from, "HEAD")
             if args.changed_from else git(root, "ls-files", "-z")).decode("utf-8").split("\0")
    for name in sorted(set(paths) - {""}):
        if args.path_prefix and not name.startswith(args.path_prefix.rstrip("/") + "/"):
            continue
        path = root / name
        data = os.readlink(path).encode("utf-8") if path.is_symlink() else path.read_bytes()
        content = "" if b"\0" in data else data.decode("utf-8", errors="replace")
        scoped = [(r, match) for r, pattern, match in rules if pattern.search(name)
                  and ("context" not in r or re.search(r["context"], content))]
        # No term pattern spans lines or anchors on them, so a term absent from the name and the whole content
        # cannot match any single line: only terms present in the file are checked line by line.
        active = []
        for term, pattern in terms:
            allowed = [allow for r, allow in scoped if term["id"] in r.get("terms", [term["id"]])]
            if None not in allowed and (pattern.search(name) or pattern.search(content)):
                active.append((term, pattern, allowed))
        if not active:
            continue
        for number, line in [(0, name), *enumerate(content.splitlines(), 1)]:
            for term, pattern, allowed in active:
                spans = [m.span() for allow in allowed for m in allow.finditer(line)]
                for match in pattern.finditer(line):
                    if any(start <= match.start() and match.end() <= end for start, end in spans):
                        continue
                    owner = owners.get(args.repo, "P8" if name.startswith("benchmark/") else "P1" if name.startswith("src/TerraScale.Authentication") else "C3" if name.startswith("docs/") else term["owner"])
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
