#!/usr/bin/env python3
"""Parallel offline checker self-tests; all core replies are SIMULATED, not full-path evidence."""

from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=True), encoding="utf-8")


def check(run, action):
    return subprocess.run([sys.executable, "-I", "-B", str(HERE / "check.py"), action, str(run)],
                          capture_output=True, timeout=15)


def run_case(kind):
    with tempfile.TemporaryDirectory(prefix=".checker-", dir=HERE / "runs") as scratch:
        run = Path(scratch)
        if kind == "auth_config":
            config = run / "core-auth.conf"
            header = 'header = "Authorization: Bearer synthetic.fixture.signature"\n'
            config.write_text(header)
            config.chmod(0o600)
            if check(run, "auth-config").returncode != 0:
                return False
            config.write_text(header + 'url = "http://127.0.0.1:1"\n')
            if check(run, "auth-config").returncode != 1:
                return False
            config.write_text(header)
            config.chmod(0o644)
            if check(run, "auth-config").returncode != 1:
                return False
            config.chmod(0o600)
            config.rename(run / "header-copy")
            config.symlink_to(run / "header-copy")
            return check(run, "auth-config").returncode == 1
        if kind == "poll_states":
            for state, code in [("analyzing", 2), ("ready", 0), ("cancelled", 1), ("invented", 1)]:
                write(run / "preview.json", {"state": state})
                if check(run, "preview-state").returncode != code:
                    return False
            for state, staged, code in [("queued", True, 2), ("running", True, 2), ("validating", True, 2),
                                        ("completed", True, 2), ("completed", False, 0), ("failed", True, 1),
                                        ("cancelled", True, 1), ("completed", 0, 1)]:
                write(run / "completed.json", {"state": state, "source": {"staged": staged}})
                if check(run, "completion-state").returncode != code:
                    return False
            return True
        if check(run, "seed").returncode != 0:
            return False
        docs = [json.loads(line) for line in (run / "seed-full.jsonl").read_text().splitlines()]
        if len(docs) != 3 or docs[0]["chunked"] != "x" * 700000:
            return False
        (run / "export.jsonl").write_text("".join(json.dumps(doc, ensure_ascii=True) + "\n" for doc in docs))
        converted = subprocess.run([sys.executable, "-I", "-B", str(HERE.parent / "convert.py"),
                                   str(run / "export.jsonl"), str(run / "converted.sql"),
                                   "--database", "mongodb_adapter_acceptance", "--collection", "records"],
                                  capture_output=True, timeout=15)
        if converted.returncode != 0 or check(run, "prepare").returncode != 0:
            return False
        expected = json.loads((run / "expected.json").read_text())
        source = {"sha256": expected["artifactSha256"], "bytes": expected["artifactBytes"], "staged": False}
        preview_id = "00000000000000000000000001"
        table = {"source": "public.mongodb_documents", "collection": "mongodb_documents", "rows": 3,
                 "excluded": False, "idPolicy": "primary_key", "primaryKey": ["id"],
                 "columns": [{"name": name, "mapping": mapping, "nullable": False, "excluded": False}
                             for name, mapping in [("id", "string"), ("document", "json"), ("provenance", "json")]]}
        if kind == "preview_mapping":
            table["columns"][1]["mapping"] = "string"
        write(run / "preview.json", {"state": "ready", "source": source,
              "preview": {"id": preview_id, "blocking": 0, "warnings": 0, "tables": [table]}})
        if kind in ("missing_collection", "wrong_404"):
            write(run / "before-start.json", {"error": {"code": "unknown_collection" if kind == "missing_collection" else "unknown_database"}})
            (run / "before-start.status").write_text("404\n")
        else:
            write(run / "before-start.json", {"items": [], "next_cursor": None})
            (run / "before-start.status").write_text("200\n")
        ready = check(run, "start-body").returncode == 0
        if kind in ("preview_mapping", "wrong_404"):
            return not ready and not (run / "start.json").exists()
        if not ready:
            return False
        checksum = "a" * 64
        if kind == "staged":
            source["staged"] = True
        validation = {"source": "public.mongodb_documents", "collection": "mongodb_documents", "sourceRows": 3,
                      "documents": 3, "match": True, "sourceChecksum": checksum,
                      "documentChecksum": "b" * 64 if kind == "core_checksum" else checksum}
        write(run / "completed.json", {"state": "completed", "error": None, "source": source,
              "confirmedPreviewId": preview_id, "progress": {"rowsWritten": 3, "rowsTotal": 3},
              "validation": {"passed": True, "tables": [validation]}})
        bodies = expected["bodies"]
        if kind == "null_missing":
            del bodies[0]["document"]["nullable"]
        if kind == "boolean_number":
            bodies[0]["document"]["nested"]["items"][1] = 1
        write(run / "documents.json", {"items": [{"id": body["id"], "state": "value", "version": "simulated",
              "document": body} for body in bodies], "next_cursor": None})
        accepted = check(run, "verify").returncode == 0
        return accepted if kind in ("success", "missing_collection") else not accepted and not (run / "verified.json").exists()


def main():
    (HERE / "runs").mkdir(mode=0o700, exist_ok=True)
    cases = ["success", "preview_mapping", "core_checksum", "null_missing", "boolean_number",
             "missing_collection", "wrong_404", "staged", "poll_states", "auth_config"]
    with ThreadPoolExecutor() as executor:
        results = list(executor.map(run_case, cases))
    print(f"{sum(results)}/{len(cases)} offline checker self-tests passed; core responses simulated; actual full-path NOT RUN")
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
