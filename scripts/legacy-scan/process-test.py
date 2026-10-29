import concurrent.futures
import json
import subprocess
import sys
import tempfile
from pathlib import Path


SCANNER = Path(__file__).with_name("scan.py")


def check(case):
    files, mode, expected, terms, *locations = case
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        subprocess.run(["git", "init", "-q", directory], check=True)
        for name, content in {".legacy-scan-mode": mode, **files}.items():
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            if isinstance(content, tuple):
                path.symlink_to(content[0])
            elif isinstance(content, bytes):
                path.write_bytes(content)
            else:
                path.write_text(content)
        subprocess.run(["git", "-C", directory, "add", "."], check=True)
        subprocess.run(["git", "-C", directory, "-c", "user.name=Scanner Test",
                        "-c", "user.email=scanner@example.invalid", "commit", "-qm", "fixture"], check=True)
        result = subprocess.run([sys.executable, str(SCANNER), "--root", directory,
                                 "--repo", "terrascale", "--output", str(root / "report.md")],
                                text=True, capture_output=True)
        assert result.returncode == expected, (result.stdout, result.stderr)
        hits = [json.loads(line) for line in result.stdout.splitlines()[1:]]
        found = {hit["term"] for hit in hits}
        assert found == terms, (found, terms, result.stdout)
        if locations:
            assert [(h["path"], h["line"], h["term"], h["owner"]) for h in hits] == locations[0], hits
        if expected != 2:
            assert f"Total hits: **{len(hits)}**" in (root / "report.md").read_text()
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
    ({"api.cs": "IDocumentStore TerraBaseDocumentsClient DocumentKey CollectionKey CreateCollectionAsync QueryDocumentsAsync"}, "fail", 1, {"documents", "collections"}),
    ({"parser.cs": 'using var document = JsonDocument.Parse("{}"); document.RootElement; document.Dispose();'}, "fail", 0, set()),
    ({"parser.cs": 'using var document = JsonDocument.Parse("{}"); document.RootElement; QueryDocumentsAsync();'}, "fail", 1, {"documents"}),
    ({"index.js": "document.appendChild(element); node.ownerDocument"}, "fail", 0, set()),
    ({"docs/terrabase/replication/home-apply.md": "HLC column LWW; last-writer-wins"}, "fail", 0, set()),
    ({"docs/terrabase/replication/home-apply.md": "leaderless"}, "fail", 1, {"leaderless"}),
    ({"old.txt": "last-writer-wins"}, "fail", 1, {"leaderless"}),
    ({"src/vendor/Turso.Data.Sqlite/src/Turso.Data.Sqlite/Properties/Resources.cs": "UnknownCollection collectionName Unknown collection; webhooks"}, "fail", 1, {"webhooks"}),
    ({"ops.sh": "flyctl storage list; flyctl deploy"}, "fail", 1, {"fly"}, [("ops.sh", 1, "fly", "E9")]),
    ({"Hrana.bin": b"\x00"}, "fail", 1, {"hrana"}, [("Hrana.bin", 0, "hrana", "E4")]),
    ({"benchmark/runner.go": "documents"}, "fail", 1, {"documents"}, [("benchmark/runner.go", 1, "documents", "P8")]),
    ({"api.cs": "document_key collection_key document_id collection_id documentKey collectionKey"}, "fail", 1, {"documents", "collections"}, [("api.cs", 1, term, "E4") for term in ["collections"] * 3 + ["documents"] * 3]),
    ({"di.cs": "IServiceCollection ServiceCollection EndpointServiceCollectionExtensions CollectionKey"}, "fail", 1, {"collections"}, [("di.cs", 1, "collections", "E4")]),
    ({"parser.cs": "using var document = await JsonDocument.ParseAsync(stream); return document;"}, "fail", 0, set()),
    ({"parser.cs": "JsonDocument document; document = await JsonDocument.ParseAsync(stream); return document; DocumentKey"}, "fail", 1, {"documents"}, [("parser.cs", 1, "documents", "E4")]),
    ({"src/vendor/Turso.Data.Sqlite/src/Turso.Data.Sqlite/Properties/Resources.cs": "Too many restrictions specified for collection {collectionName}. DocumentKey"}, "fail", 1, {"documents"}),
    ({"src/vendor/Turso.Data.Sqlite/src/Turso.Data.Sqlite/SqliteConnection.cs": "DbMetaDataCollectionNames.MetaDataCollections CollectionName collectionName UnknownCollection; webhooks"}, "fail", 1, {"webhooks"}),
    ({"Hrana.txt": "SQL rows"}, "fail", 1, {"hrana"}),
]
for name in ["src/TerraScale.Endpoints/RequestBinder.cs", "src/vendor/Turso.Data.Sqlite/src/Turso.Data.Sqlite/Properties/Resources.cs", "src/vendor/Turso.Data.Sqlite/src/Turso.Data.Sqlite/SqliteConnection.cs"]:
    source = Path(__file__).resolve().parents[2] / name
    cases.append(({name: source.read_text()}, "fail", 0, set()))
with concurrent.futures.ThreadPoolExecutor() as executor:
    list(executor.map(check, cases))
print(f"Passed {len(cases)} parallel scanner process integration cases")
