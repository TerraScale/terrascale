#!/usr/bin/env python3
"""Execute the registered alternating host-process CRUD diagnostic once."""

import hashlib
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
RUN = Path(__file__).resolve().parent
PLAN = json.loads((RUN / "run-plan.json").read_text())
BIN = REPO / "local-engine/target/release/local-engine-scorecard"
MANIFEST_SCRIPT = REPO / "local-engine/host-manifest.py"
PINNED_TURSO = "64b8ef5742fc18937f9c89806c81e3f6475dc7a3"
EXPECTED_BINARY = "e272a685309c59ed7155abd574848b704daf56e5b0894f699a31717aa1ca348f"
EXPECTED_MAIN = "d9ffd9755796351b25000ec8a18c44ce6b66e549cf21f97d26e953ad9de4a5a1"
EXPECTED_MANIFEST_SCRIPT = "e3477cdee93e6f1723c233258505237d7e20f892fc4fa414f1806dc113cf9f78"
EXPECTED_LOCK = "56bb44266af8ebe02b5e8da14f233e5a65a3565eb7e830552897a7b70d1cca97"
EXPECTED_DIFF = "3860cb7bb959179a6e651e1b00ca17f647787d2f4c35d2974ecd23274fe7c55b"
EXPECTED_PLAN = "ae6d3242c8c3b38691e39cc8f7a95366674ecdbc6d7e21cfce2cb7eefca8c25d"
EXPECTED_PAIRS = [["sqlite", "turso"], ["turso", "sqlite"], ["sqlite", "turso"], ["turso", "sqlite"]]


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require(condition: bool, description: str) -> None:
    if not condition:
        raise RuntimeError(description)


def run_command(command: list[str], lane: Path, label: str) -> None:
    started = datetime.now(timezone.utc).isoformat()
    with (lane / f"{label}.stdout.log").open("x") as stdout:
        with (lane / f"{label}.stderr.log").open("x") as stderr:
            completed = subprocess.run(command, cwd=REPO, stdout=stdout, stderr=stderr, check=False)
    record = {
        "argv": command,
        "cwd": str(REPO),
        "started_utc": started,
        "finished_utc": datetime.now(timezone.utc).isoformat(),
        "exit_code": completed.returncode,
    }
    with (lane / f"{label}.command.json").open("x") as output:
        json.dump(record, output, indent=2)
        output.write("\n")
    if completed.returncode:
        raise RuntimeError(f"{label} failed in {lane}: exit {completed.returncode}")


def main() -> None:
    require(sha256(RUN / "run-plan.json") == EXPECTED_PLAN, "registered run plan changed")
    require(PLAN["pairs"] == EXPECTED_PAIRS, "run order differs from registered four pairs")
    require(PLAN["workload"] == {"cycles": 1000, "body_bytes": 256, "seed": 1, "operations_per_cycle": 7, "warmup_cycles": 0}, "workload differs from registered plan")
    require(PLAN["cpu_affinity"] == "20", "CPU affinity differs from registered plan")
    if sha256(REPO / "local-engine/src/main.rs") != EXPECTED_MAIN:
        raise RuntimeError("driver source differs from reviewed freeze")
    if sha256(MANIFEST_SCRIPT) != EXPECTED_MANIFEST_SCRIPT:
        raise RuntimeError("host manifest script differs from reviewed freeze")
    if sha256(REPO / "local-engine/Cargo.lock") != EXPECTED_LOCK:
        raise RuntimeError("dependency lock differs from reviewed freeze")
    if sha256(RUN / "source-snapshot/turso-tracked.patch") != EXPECTED_DIFF:
        raise RuntimeError("Turso patch differs from reviewed freeze")
    binary_hash = sha256(BIN)
    require(binary_hash == EXPECTED_BINARY, "binary differs from the final smoke freeze")
    seen_trace = None
    seen_final = None
    seen_filesystem = None
    verified_lanes = 0
    for trial, pair in enumerate(PLAN["pairs"], start=1):
        for engine in pair:
            lane = RUN / f"pair-{trial:02d}-{engine}"
            lane.mkdir()
            prefix = ["nice", "-n", "19", "ionice", "-c", "3", "taskset", "-c", PLAN["cpu_affinity"]]
            manifest = lane / "environment.json"
            run_command(
                prefix + [sys.executable, str(MANIFEST_SCRIPT), "--output", str(manifest), "--database-parent", str(lane)],
                lane,
                "manifest",
            )
            command = prefix + [
                "timeout", "180s", str(BIN), "--engine", engine,
                "--cycles", "1000", "--body-bytes", "256", "--seed", "1",
                "--trial", str(trial), "--database", str(lane / "data.db"),
                "--output", str(lane / "results"),
                "--environment-manifest", str(manifest),
                "--benchmark-revision", PLAN["benchmark_head"],
                "--terrabase-revision", PLAN["terrabase_head"],
                "--turso-revision", PINNED_TURSO,
            ]
            run_command(command, lane, "driver")
            receipt = json.loads((lane / "results/receipt.json").read_text())
            config = receipt["configuration"]
            require(receipt["status"] == "passed", f"{lane}: receipt failed")
            require(receipt["operations_completed"] == 7000, f"{lane}: operation count differs")
            require(receipt["final_rows"] == 1000, f"{lane}: final row count differs")
            require(receipt["integrity_check"] == "ok", f"{lane}: integrity check failed")
            require(receipt["binary_sha256"] == binary_hash, f"{lane}: binary hash differs")
            require(receipt["driver_source_sha256"] == EXPECTED_MAIN, f"{lane}: driver source differs")
            require(receipt["dependency_lock_sha256"] == EXPECTED_LOCK, f"{lane}: lock differs")
            require(receipt["turso_tracked_diff_sha256"] == EXPECTED_DIFF, f"{lane}: Turso patch differs")
            require(receipt["environment_manifest_sha256"] == sha256(manifest), f"{lane}: environment manifest differs")
            require(receipt["environment_manifest"]["cpu_affinity"] == PLAN["cpu_affinity"], f"{lane}: CPU affinity differs")
            require(receipt["environment_manifest"]["process_nice"] == 19, f"{lane}: process priority differs")
            require(receipt["environment_manifest"]["database_parent"] == str(lane.resolve()), f"{lane}: database parent differs")
            require(config["page_size"] == 4096, f"{lane}: page size differs")
            require(config["journal_mode"].lower() == "wal", f"{lane}: journal mode differs")
            require(config["synchronous"] == 2, f"{lane}: synchronous setting differs")
            require(config["cache_kib"] == -65536, f"{lane}: cache setting differs")
            require(config["wal_autocheckpoint_pages_readback"] == (1000 if engine == "sqlite" else None), f"{lane}: checkpoint readback differs")
            require(sum(item["samples"] for item in receipt["summaries"]) == 7000, f"{lane}: summary sample count differs")
            require(sum(1 for _ in (lane / "results/samples.jsonl").open()) == 7000, f"{lane}: raw sample count differs")
            trace = receipt["trace_sha256"]
            final = receipt["final_state_sha256"]
            filesystem = receipt["environment_manifest"]["filesystem"]
            if seen_trace is None:
                seen_trace, seen_final, seen_filesystem = trace, final, filesystem
            require((trace, final, filesystem) == (seen_trace, seen_final, seen_filesystem), f"{lane}: trace, final state or filesystem differs")
            require(sha256(BIN) == binary_hash, f"{lane}: binary changed during the run")
            verified_lanes += 1
            print(f"verified pair {trial:02d} {engine}", flush=True)
    require(verified_lanes == 8, "fewer than eight lanes were verified")
    with (RUN / "all-lanes-verified.json").open("x") as output:
        json.dump({"binary_sha256": binary_hash, "trace_sha256": seen_trace, "final_state_sha256": seen_final, "verified_lanes": verified_lanes}, output, indent=2)
        output.write("\n")


if __name__ == "__main__":
    main()
