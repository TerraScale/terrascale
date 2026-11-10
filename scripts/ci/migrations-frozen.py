#!/usr/bin/env python3
import subprocess
import sys
from pathlib import PurePosixPath


def git(*args):
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def migration_folders(revision):
    return {
        str(path.parent)
        for path in map(PurePosixPath, git("ls-tree", "-r", "--name-only", revision).splitlines())
        if path.parent.name == "Migrations" and path.name.endswith("ModelSnapshot.cs")
    }


def frozen(path, folders):
    path = PurePosixPath(path)
    return str(path.parent) in folders and not path.name.endswith("ModelSnapshot.cs")


def main():
    if len(sys.argv) != 2:
        raise SystemExit("usage: migrations-frozen.py <base-revision>")
    base = sys.argv[1]
    folders = migration_folders(base)
    changes = git("diff", "--name-status", "--find-renames", base, "HEAD").splitlines()
    violations = []
    for line in changes:
        status, *paths = line.split("\t")
        if status[0] in "MDRT" and frozen(paths[0], folders):
            violations.append(f"{status[0]} {' -> '.join(paths)}")
    if violations:
        print("A merged EF Core migration is immutable: never edit, rename or delete it; add a new migration instead.")
        print("\n".join(violations))
        raise SystemExit(1)
    print(f"migrations-frozen: ok ({len(folders)} migration folders, no merged migration changed)")


main()
