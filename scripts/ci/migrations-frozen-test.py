#!/usr/bin/env python3
import subprocess
import tempfile
from pathlib import Path

script = Path(__file__).resolve().with_name("migrations-frozen.py")


def run(repo, *args):
    return subprocess.run(args, cwd=repo, check=False, capture_output=True, text=True)


def commit(repo, files, message):
    for name, text in files.items():
        path = repo / name
        if text is None:
            path.unlink()
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text)
    run(repo, "git", "add", "-A")
    assert run(repo, "git", "commit", "-qm", message).returncode == 0


def check(repo):
    return run(repo, "python3", str(script), "main")


migrations = "src/App/Migrations"
with tempfile.TemporaryDirectory() as directory:
    repo = Path(directory)
    run(repo, "git", "init", "-qb", "main")
    run(repo, "git", "config", "user.email", "ci@terrascale.tech")
    run(repo, "git", "config", "user.name", "ci")
    commit(repo, {
        f"{migrations}/20261109181757_CreateAuthFlows.cs": "create auth_flows",
        f"{migrations}/20261109181757_CreateAuthFlows.Designer.cs": "designer auth_flows",
        f"{migrations}/AppDbContextModelSnapshot.cs": "snapshot 1",
        "sdk/Ef/Migrations/HistoryRepository.cs": "provider code",
    }, "base")

    run(repo, "git", "checkout", "-qb", "new-migration")
    commit(repo, {
        f"{migrations}/20261109224524_CreateAccountDeletions.cs": "create account_deletions",
        f"{migrations}/20261109224524_CreateAccountDeletions.Designer.cs": "designer account_deletions",
        f"{migrations}/AppDbContextModelSnapshot.cs": "snapshot 2",
        "sdk/Ef/Migrations/HistoryRepository.cs": "provider code changed",
    }, "new migration")
    result = check(repo)
    assert result.returncode == 0, result.stdout + result.stderr

    run(repo, "git", "checkout", "-qb", "rewritten", "main")
    commit(repo, {
        f"{migrations}/20261109181757_CreateAuthFlows.cs": None,
        f"{migrations}/20261109224524_CreateAuthFlows.cs": "create auth_flows",
        f"{migrations}/20261109181757_CreateAuthFlows.Designer.cs": "designer auth_flows regenerated",
    }, "rewrite merged migration")
    result = check(repo)
    assert result.returncode == 1, result.stdout + result.stderr
    assert f"R {migrations}/20261109181757_CreateAuthFlows.cs -> {migrations}/20261109224524_CreateAuthFlows.cs" in result.stdout, result.stdout
    assert f"M {migrations}/20261109181757_CreateAuthFlows.Designer.cs" in result.stdout, result.stdout
print("migrations-frozen-test: ok (new migration passes; renamed and edited merged migrations fail)")
