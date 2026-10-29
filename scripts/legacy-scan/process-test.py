import concurrent.futures
import json
import subprocess
import sys
import tempfile
from pathlib import Path


SCANNER = Path(__file__).with_name("scan.py")


def check(case):
    files, mode, expected, terms = case
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        subprocess.run(["git", "init", "-q", directory], check=True)
        for name, content in {".legacy-scan-mode": mode, **files}.items():
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            if isinstance(content, tuple):
                path.symlink_to(content[0])
            else:
                path.write_text(content)
        subprocess.run(["git", "-C", directory, "add", "."], check=True)
        subprocess.run(["git", "-C", directory, "-c", "user.name=Scanner Test",
                        "-c", "user.email=scanner@example.invalid", "commit", "-qm", "fixture"], check=True)
        result = subprocess.run([sys.executable, str(SCANNER), "--root", directory,
                                 "--repo", "terrascale", "--output", str(root / "report.md")],
                                text=True, capture_output=True)
        assert result.returncode == expected, (result.stdout, result.stderr)
        found = {json.loads(line)["term"] for line in result.stdout.splitlines()[1:]}
        assert found == terms, (found, terms, result.stdout)
        if expected != 2:
            assert "Owner task" in (root / "report.md").read_text()


cases = [
    ({"api.txt": "collections documents feeds lattices leaderless Hrana /libsql custom wire protocol webhooks Fly.io Turso IDocumentService"},
     "fail", 1, {"collections", "documents", "document-feeds", "lattices", "leaderless", "hrana", "libsql", "custom-wire", "webhooks", "fly", "engine-brand"}),
    ({"api.txt": "Hrana"}, "report", 0, {"hrana"}),
    ({"api.txt": "JsonDocument System.Collections.Generic GC collections Postgres wire protocol"}, "fail", 0, set()),
    ({"api.txt": "JsonDocument and documents; System.Collections and collections"}, "fail", 1, {"documents", "collections"}),
    ({"src/vendor/engine.rs": "Turso libsql", "docs/archive/old.md": "Hrana documents"}, "fail", 0, set()),
    ({"src/vendor/engine.rs": "webhooks", "docs/active.md": "Turso"}, "fail", 1, {"webhooks", "engine-brand"}),
    ({"index.js": "document.querySelector('main'); document.body"}, "fail", 0, set()),
    ({"index.js": "document.querySelector('main'); documents"}, "fail", 1, {"documents"}),
    ({"api.txt": "SQL rows"}, "invalid", 2, set()),
    ({"shortcut": ("/unreadable/outside/Hrana",)}, "fail", 1, {"hrana"}),
    ({"shortcut": ("api.txt",), "api.txt": "webhooks"}, "fail", 1, {"webhooks"}),
    ({"Hrana.txt": "SQL rows"}, "fail", 1, {"hrana"}),
]
with concurrent.futures.ThreadPoolExecutor() as executor:
    list(executor.map(check, cases))
print(f"Passed {len(cases)} parallel scanner process integration cases")
