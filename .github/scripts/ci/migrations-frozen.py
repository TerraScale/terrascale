#!/usr/bin/env python3
import subprocess
import sys
from pathlib import PurePosixPath


def git(*args):
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def tracked(revision):
    return [PurePosixPath(path) for path in git("ls-tree", "-r", "--name-only", revision).splitlines()]


def migration_folders(revision):
    return {
        str(path.parent)
        for path in tracked(revision)
        if path.parent.name == "Migrations" and path.name.endswith("ModelSnapshot.cs")
    }


def projects(revision):
    return {str(path) for path in tracked(revision) if path.suffix == ".csproj"}


def frozen(path, folders):
    path = PurePosixPath(path)
    return str(path.parent) in folders and not path.name.endswith("ModelSnapshot.cs")


def project_deleted(path, base_projects, head_projects, deleted):
    parents = {str(parent) for parent in PurePosixPath(path).parents}
    owners = {project for project in base_projects if str(PurePosixPath(project).parent) in parents}
    remaining = {project for project in head_projects if str(PurePosixPath(project).parent) in parents}
    return bool(owners) and owners <= deleted and not remaining


def main():
    if len(sys.argv) != 2:
        raise SystemExit("usage: migrations-frozen.py <base-revision>")
    base = sys.argv[1]
    folders = migration_folders(base)
    base_projects = projects(base)
    head_projects = projects("HEAD")
    changes = [line.split("\t") for line in git("diff", "--name-status", "--find-renames", base, "HEAD").splitlines()]
    deleted = {paths[0] for status, *paths in changes if status == "D"}
    violations = []
    for status, *paths in changes:
        if status[0] not in "MDRT" or not frozen(paths[0], folders):
            continue
        if status == "D" and project_deleted(paths[0], base_projects, head_projects, deleted):
            continue
        violations.append(f"{status[0]} {' -> '.join(paths)}")
    if violations:
        print("A merged EF Core migration is immutable: never edit, rename or delete it; add a new migration instead.")
        print("\n".join(violations))
        raise SystemExit(1)
    print(f"migrations-frozen: ok ({len(folders)} migration folders, no merged migration changed)")


main()
