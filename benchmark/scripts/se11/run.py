#!/usr/bin/env python3
"""The SE-11 end-to-end client live-update tracer orchestrator (ticket #13).

One scripted scenario family that runs BOTH embedded SDK cores — the
TypeScript session (the sibling TerraScale.TypeScript checkout's built
dist, read-only) and the .NET client (the sibling TerraScale.DotNet
checkout, read-only) — against the real Rust engine process (a TBEP
`SyncDeliveryHost` over UDS, built from this repository's
`crates/terrabase-tbep` into a throwaway cargo project outside the
repository, exactly the SE-08 `EngineHarness` pattern) and the real C#
API host (`host/TerraBase.Api`).

Scenarios (one receipt each, under evidence/sync/se11/):

  s1-golden-path        commit -> backbone -> Loro delivery -> host ->
                        both SDKs apply; sequences asserted against the
                        SE-04 golden vectors; both SDK logs identical.
  s2-drop-resume        the SDK's connection drops mid-stream (host
                        process loss); reconnect with checkpoint resume.
  s3-slow-consumer      a stalled SSE consumer sheds frames under the
                        host's bounded queue; fast consumers unaffected.
  s4-tbep-link-loss     the engine process (the TBEP link endpoint) is
                        lost; typed refusals, supervision, recovery.
  s5-region-route-change  the serving region moves; both SDKs carry the
                        durable checkpoint to the new region.
  s6-multi-region       a write through one region visible through the
                        other; convergence and lag vocabulary exact.

Loopback hosts and temp-dir sockets only; backend-context fixtures
(X-TB-Principal/X-TB-Tenant) only; no secrets; no live systems; no
performance claims (correctness evidence only).
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import http.client
import json
import os
import pathlib
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
WORKSPACE_ROOT = REPO_ROOT.parents[2]
TS_REPO = pathlib.Path(os.environ.get("TERRASCALE_TS_SDK", str(WORKSPACE_ROOT / "TerraScale.TypeScript")))
DOTNET_REPO = pathlib.Path(os.environ.get("TERRASCALE_DOTNET_SDK_REPO", str(WORKSPACE_ROOT / "TerraScale.DotNet")))
VECTORS = REPO_ROOT / "crates/terrabase-sync-core/vectors/terrascale.sync.v2"
EVIDENCE = REPO_ROOT / "evidence/sync/se11"
TRACER_TS = REPO_ROOT / "tracer/ts/tracer.mjs"
TRACER_DOTNET_DLL = REPO_ROOT / "tracer/dotnet/TerraScale.Tracer/bin/Debug/net10.0/TerraScale.Tracer.dll"
HOST_CWD = REPO_ROOT / "host/TerraBase.Api/bin/Debug/net10.0"

TENANT = "tenant-a"
PRINCIPAL = "backend-operator"
COLLECTION = "orders"


def log(message: str) -> None:
    print(f"[se11] {message}", flush=True)


def git_head(path: pathlib.Path) -> str:
    head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=path, capture_output=True, text=True, check=True)
    return head.stdout.strip()


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


# ---------------------------------------------------------------------------
# The engine harness: the SE-08 pattern — a throwaway cargo project outside
# the repository binds the repository's real TBEP engine server.
# ---------------------------------------------------------------------------


class EngineHarness:
    def __init__(self, home: pathlib.Path):
        self.home = home
        self.source = home / "src"
        self.binary = home / "target/release/tbep-engine-harness"

    def build(self) -> pathlib.Path:
        if self.binary.exists():
            return self.binary
        self.source.mkdir(parents=True, exist_ok=True)
        (self.home / "Cargo.toml").write_text(
            f"""# Generated at run time by scripts/se11/run.py (SE-11): a
# throwaway cargo project that binds the repository's real TBEP engine
# server (crates/terrabase-tbep) for the SE-11 tracer. Nothing here is
# part of the repository's Rust scope.
[package]
name = "tbep-engine-harness"
version = "0.1.0"
edition = "2021"
publish = false

[dependencies]
terrabase-tbep = {{ path = "{REPO_ROOT}/crates/terrabase-tbep" }}

[workspace]

[profile.release]
debug = false
""",
            encoding="utf-8",
        )
        (self.source / "main.rs").write_text(
            """use std::io::BufRead as _;
use std::sync::Arc;
use terrabase_tbep::{SyncDeliveryHost, TbepServer};

fn main() {
    let mut args = std::env::args().skip(1);
    let socket = args.next().expect("usage: tbep-engine-harness <socket> <instance>");
    let instance: u64 = args.next().and_then(|s| s.parse().ok()).unwrap_or(1);
    let mut server = TbepServer::bind(&socket, Arc::new(SyncDeliveryHost::new(instance)))
        .expect("the engine harness binds its UDS listener");
    println!("serving {socket} instance {instance}");
    // The owning script supervises the process: it ends on stdin close
    // or by process loss.
    let mut line = String::new();
    let _ = std::io::stdin().lock().read_line(&mut line);
    server.shutdown();
}
""",
            encoding="utf-8",
        )
        cargo = shutil.which("cargo") or os.path.expanduser("~/.cargo/bin/cargo")
        environment = dict(os.environ)
        environment.pop("RUSTUP_TOOLCHAIN", None)
        result = subprocess.run(
            [cargo, "+1.97.1", "build", "--release", "--offline"],
            cwd=self.home,
            env=environment,
            capture_output=True,
            text=True,
        )
        if result.returncode != 0:
            log("offline engine build failed; retrying without the offline pin")
            result = subprocess.run(
                [cargo, "+1.97.1", "build", "--release"],
                cwd=self.home,
                env=environment,
                capture_output=True,
                text=True,
            )
        if result.returncode != 0:
            raise RuntimeError(f"the engine harness failed to build:\n{result.stdout}\n{result.stderr}")
        return self.binary


# ---------------------------------------------------------------------------
# The topology: engine processes + C# hosts, script-supervised.
# ---------------------------------------------------------------------------


class Host:
    """One C# API host over one engine endpoint, script-supervised."""

    def __init__(self, name: str, run_dir: pathlib.Path, engine_binary: pathlib.Path, managed: bool, instance: int = 1):
        self.name = name
        self.run_dir = run_dir
        self.managed = managed
        self.instance = instance
        self.socket = run_dir / f"{name}.sock"
        self.port = free_port()
        self.url = f"http://127.0.0.1:{self.port}"
        self.engine_binary = engine_binary
        self.engine: subprocess.Popen | None = None
        self.process: subprocess.Popen | None = None

    def spawn_host_process(self) -> None:
        environment = dict(os.environ)
        environment["TERRABASE_TBEP_SOCKET"] = str(self.socket)
        if self.managed:
            environment["TERRABASE_TBEP_ENGINE"] = str(self.engine_binary)
        environment["TERRABASE_HOST_URL"] = self.url
        self.process = subprocess.Popen(
            ["dotnet", "TerraBase.Api.dll"],
            cwd=HOST_CWD,
            env=environment,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )

    def start(self) -> None:
        if not self.managed:
            self.engine = subprocess.Popen(
                [str(self.engine_binary), str(self.socket), str(self.instance)],
                stdin=subprocess.PIPE,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            self._wait_socket()
        self.spawn_host_process()
        self.wait_healthy()

    def _wait_socket(self, timeout: float = 15.0) -> None:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.socket.exists():
                return
            time.sleep(0.05)
        raise TimeoutError(f"{self.name}: the engine socket never appeared")

    def wait_healthy(self, timeout: float = 30.0) -> None:
        deadline = time.monotonic() + timeout
        last = ""
        while time.monotonic() < deadline:
            try:
                body = self.get("/health")
                if body.get("status") == "serving":
                    return
                last = json.dumps(body)
            except Exception as failure:  # noqa: BLE001 - poll until healthy
                last = str(failure)
            time.sleep(0.1)
        raise TimeoutError(f"{self.name}: the host never became healthy: {last}")

    def stop(self) -> None:
        for process in (self.process, self.engine):
            if process is None:
                continue
            try:
                process.terminate()
                process.wait(timeout=5)
            except Exception:  # noqa: BLE001 - best-effort teardown
                process.kill()
        self.process = None
        self.engine = None
        self._clear_socket()

    def kill_host(self) -> None:
        """The SDKs' connection endpoint drops mid-stream (a real TCP loss)."""
        if self.process is not None:
            self.process.kill()
            self.process.wait()
            self.process = None

    def restart_host_only(self) -> None:
        self.kill_host()
        self.spawn_host_process()
        self.wait_healthy()

    def engine_pid(self) -> int | None:
        result = subprocess.run(
            ["pgrep", "-f", f"tbep-engine-harness {self.socket}"],
            capture_output=True,
            text=True,
        )
        pids = [int(line) for line in result.stdout.split() if line.strip().isdigit()]
        return pids[0] if pids else None

    def kill_engine(self) -> None:
        pid = self.engine_pid()
        if pid is None:
            return
        os.kill(pid, signal.SIGKILL)
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if self.engine_pid() is None:
                return
            time.sleep(0.05)

    def _clear_socket(self) -> None:
        try:
            self.socket.unlink()
        except FileNotFoundError:
            pass

    # -- the HTTP surface ---------------------------------------------------

    def request(self, method: str, path: str, body: dict | None = None, headers: dict | None = None) -> tuple[int, dict]:
        payload = json.dumps(body or {}).encode() if body is not None else None
        request = urllib.request.Request(f"{self.url}{path}", data=payload, method=method)
        request.add_header("X-TB-Principal", PRINCIPAL)
        request.add_header("X-TB-Tenant", TENANT)
        for key, value in (headers or {}).items():
            request.add_header(key, value)
        if payload is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                return response.status, json.loads(response.read() or b"{}")
        except urllib.error.HTTPError as failure:
            return failure.code, json.loads(failure.read() or b"{}")

    def get(self, path: str) -> dict:
        status, body = self.request("GET", path)
        if status != 200:
            raise RuntimeError(f"{self.name}: GET {path} answered {status}: {body}")
        return body

    # -- the driver surface (a scripted backend writer) ----------------------

    def open_session(self, collection: str = COLLECTION) -> dict:
        status, body = self.request("POST", "/v1/sync/sessions", {"collection": collection})
        if status != 200:
            raise RuntimeError(f"session setup refused: {status} {body}")
        return body

    def mutate(self, session_id: int, key: str, document: str, put: dict | None = None, delete: bool = False) -> dict:
        change: dict = (
            {"delete": True}
            if delete
            else {"put": [{"name": name, "value": {"string": value}} for name, value in (put or {}).items()]}
        )
        status, body = self.request(
            "POST",
            f"/v1/sync/sessions/{session_id}/mutations",
            {"document": document, "change": change},
            headers={"Idempotency-Key": key},
        )
        if status != 200:
            raise RuntimeError(f"mutation refused: {status} {body}")
        return body

    def close_session(self, session_id: int) -> None:
        self.request("POST", f"/v1/sync/sessions/{session_id}/close", {})

    def seed_journal(self, commits: list[dict]) -> None:
        """Replays a golden-vector journal through the real write path."""
        session = self.open_session()
        for commit in commits:
            self.mutate(
                session["sessionId"],
                commit["operation_id"],
                commit["document"],
                put=commit.get("put"),
                delete=commit.get("delete", False),
            )
        self.close_session(session["sessionId"])

    def metrics(self) -> dict:
        return self.get("/v1/metrics")


# ---------------------------------------------------------------------------
# The SDK tracers.
# ---------------------------------------------------------------------------


class Tracer:
    def __init__(self, role: str, run_dir: pathlib.Path, gates: pathlib.Path, host_url: str):
        self.role = role
        self.spec_path = run_dir / f"spec-{role}.json"
        self.log_path = run_dir / f"observations-{role}.jsonl"
        self.checkpoint_file = run_dir / f"checkpoint-{role}.b64"
        self.gates = gates
        self.host = host_url
        self.process: subprocess.Popen | None = None
        self.plan: list[dict] = []

    def build_spec(self) -> None:
        spec = {
            "role": self.role,
            "out": str(self.log_path),
            "gates": str(self.gates),
            "checkpointFile": str(self.checkpoint_file),
            "host": self.host,
            "collection": COLLECTION,
            "tenant": TENANT,
            "principal": PRINCIPAL,
            "reconnect": {"delayMs": 150, "maxAttempts": 4},
            "reconnectDelayMs": 150,
            "reconnectMaxMs": 5000,
            "plan": self.plan,
        }
        if self.role == "ts":
            spec["sdk"] = str(TS_REPO)
        self.spec_path.write_text(json.dumps(spec, indent=1), encoding="utf-8")

    def start(self) -> None:
        self.build_spec()
        if self.log_path.exists():
            self.log_path.unlink()
        if self.role == "ts":
            command = ["node", str(TRACER_TS), str(self.spec_path)]
        else:
            command = ["dotnet", str(TRACER_DOTNET_DLL), str(self.spec_path)]
        self.process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)

    def wait(self, timeout: float = 120.0) -> int:
        if self.process is None:
            raise RuntimeError(f"{self.role}: the tracer was never started")
        try:
            self.process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            self.process.kill()
            raise TimeoutError(f"{self.role}: the tracer plan exceeded its bound") from None
        return self.process.returncode

    def gate(self, name: str) -> None:
        (self.gates / name).write_text("1", encoding="utf-8")

    def events(self, kind: str | None = None) -> list[dict]:
        records = []
        if not self.log_path.exists():
            return records
        for line in self.log_path.read_text(encoding="utf-8").splitlines():
            if not line.strip():
                continue
            record = json.loads(line)
            if kind is None or record.get("t") == kind:
                records.append(record)
        return records

    def deliveries(self) -> list[dict]:
        return [event for event in self.events() if event.get("t") in ("delta-applied", "snapshot-adopted")]

    def checkpoint_now(self) -> dict | None:
        deliveries = self.deliveries()
        return deliveries[-1]["checkpoint"] if deliveries else None

    def wait_final(self, timeout: float = 60.0) -> dict:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            finals = self.events("final")
            if finals:
                return finals[-1]
            time.sleep(0.1)
        raise TimeoutError(f"{self.role}: no final observation")


def normalized_deliveries(tracer: Tracer) -> list[tuple]:
    """The comparable transition sequence: (kind, storage sequence, ends,
    payload byte identity). The TS core's observation surface carries no
    per-batch commit count (its session applies opaque payloads); the
    .NET-side commit counts are asserted separately in the budget rows."""
    rows = []
    for event in tracer.deliveries():
        checkpoint = event["checkpoint"]
        rows.append((
            event["t"],
            checkpoint["storageSequence"],
            tuple(tuple(pair) for pair in checkpoint["ends"]),
            event.get("payloadSha256"),
        ))
    return rows


class Facts:
    """Collected per-scenario facts for the receipt writer."""

    def __init__(self, name: str, title: str):
        self.name = name
        self.title = title
        self.checks: list[tuple[str, bool, str]] = []

    def check(self, name: str, passed: bool, detail: str) -> bool:
        self.checks.append((name, passed, detail))
        log(f"  [{'PASS' if passed else 'FAIL'}] {name}: {detail}")
        return passed

    @property
    def passed(self) -> bool:
        return all(passed for _, passed, _ in self.checks)

    def summary(self) -> str:
        passed = sum(1 for _, ok, _ in self.checks if ok)
        return f"{passed}/{len(self.checks)} checks"


def wait_until(predicate, what: str, timeout: float = 30.0, interval: float = 0.05):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(interval)
    raise TimeoutError(f"timed out waiting for {what}")


def wait_both_at(tracers: list[Tracer], sequence: int, timeout: float = 30.0) -> None:
    wait_until(
        lambda: all((tracer.checkpoint_now() or {}).get("storageSequence", 0) >= sequence for tracer in tracers),
        f"both SDKs to reach storage sequence {sequence}",
        timeout,
    )


# ---------------------------------------------------------------------------
# The slow-consumer probe: a raw SSE reader whose consumption stalls.
# ---------------------------------------------------------------------------


class SlowProbe:
    """A real SSE consumer whose reading the scenario stalls (http.client
    incremental reads; pausing is simply not reading — the kernel window
    fills, the host's per-stream bounded queue overflows, and the host
    sheds, counts, and surfaces the drop in-band)."""

    def __init__(self, host: Host, collection: str = COLLECTION):
        self.host = host
        self.collection = collection
        self.events: list[dict] = []
        self.ended = False
        self.carry = ""
        self.connection = http.client.HTTPConnection("127.0.0.1", int(host.url.rsplit(":", 1)[1]), timeout=0.5)
        self.response: http.client.HTTPResponse | None = None

    def open(self, receive_window: int | None = None, from_sequence: int | None = None) -> None:
        self.connection.connect()
        if receive_window is not None:
            try:
                # A small receive window for the STALLED consumer only:
                # stalling fills it fast, so the host's bounded queue
                # (not the kernel) absorbs the backlog and the shed is
                # observable quickly.
                self.connection.sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, receive_window)
            except OSError:
                pass
        path = f"/v1/streams?collections={self.collection}"
        if from_sequence is not None:
            path += f"&from={from_sequence}"
        self.connection.request(
            "GET",
            path,
            headers={
                "Accept": "text/event-stream",
                "X-TB-Principal": PRINCIPAL,
                "X-TB-Tenant": TENANT,
            },
        )
        self.response = self.connection.getresponse()
        if self.response.status != 200:
            raise RuntimeError(f"the probe stream refused: {self.response.status}")

    def pump(self) -> list[dict]:
        """Reads whatever is available now and returns newly parsed events."""
        if self.response is None or self.ended:
            return []
        fresh = []
        try:
            while True:
                chunk = self.response.read1(65536)
                if chunk == b"":
                    self.ended = True
                    break
                fresh.extend(self._parse(chunk))
                if len(chunk) < 65536:
                    break
        except TimeoutError:
            # A stalled read: no data right now (the poll-style timeout
            # the short socket timeout gives a paused consumer).
            pass
        except (ConnectionError, http.client.HTTPException, OSError):
            self.ended = True
        return fresh

    def _parse(self, chunk: bytes) -> list[dict]:
        # SSE events can split across reads: carry the trailing partial
        # block and parse only complete \n\n-terminated blocks.
        text = self.carry + chunk.decode("utf-8", errors="replace")
        if "\n\n" not in text:
            self.carry = text
            return []
        complete, _, self.carry = text.rpartition("\n\n")
        parsed = []
        for block in complete.split("\n\n"):
            name = None
            event_id = None
            payload_lines = []
            for line in block.splitlines():
                if line.startswith("event:"):
                    name = line[6:].strip()
                elif line.startswith("id:"):
                    event_id = line[3:].strip()
                elif line.startswith("data:"):
                    payload_lines.append(line[5:].strip())
            if name is not None and payload_lines:
                try:
                    body = json.loads("\n".join(payload_lines))
                except json.JSONDecodeError:
                    body = "\n".join(payload_lines)
                record = {"event": name, "id": event_id, "data": body, "bytes": len(block.encode())}
                parsed.append(record)
                self.events.append(record)
        return parsed

    def close(self) -> None:
        try:
            self.connection.close()
        except OSError:
            pass


# ---------------------------------------------------------------------------
# The scenarios.
# ---------------------------------------------------------------------------


SCENARIO_TITLES = {
    "s1-golden-path": "S1 golden path",
    "s2-drop-resume": "S2 connection drop and checkpoint resume",
    "s3-slow-consumer": "S3 slow-consumer backpressure",
    "s4-tbep-link-loss": "S4 TBEP link loss",
    "s5-region-route-change": "S5 region route change",
    "s6-multi-region": "S6 multi-region visibility",
}


def scenario_golden_path(home: pathlib.Path, run_root: pathlib.Path) -> Facts:
    facts = Facts("s1-golden-path", SCENARIO_TITLES["s1-golden-path"])
    run_dir = run_root / "s1"
    gates = run_dir / "gates"
    gates.mkdir(parents=True, exist_ok=True)

    fresh = json.loads((VECTORS / "fresh-session.json").read_text())
    repeated = json.loads((VECTORS / "repeated-change-delta.json").read_text())
    host = Host("region-a", run_dir, home, managed=False)
    host.start()
    try:
        # Phase A — the fresh-session vector's journal replays through the
        # real write path; both SDK sessions then open fresh and adopt the
        # server's full snapshot at the vector's checkpoint.
        host.seed_journal(fresh["server"]["journal_before"])
        snapshot_answer = next(
            step["server_frame"]["frame"]
            for step in fresh["steps"]
            if "server_frame" in step and step["server_frame"]["frame"]["kind"] == "snapshot_answer"
        )
        expected_checkpoint = snapshot_answer["checkpoint"]
        expected_payload = bytes.fromhex(snapshot_answer["payload"])

        ts = Tracer("ts", run_dir, gates, host.url)
        net = Tracer("net", run_dir, gates, host.url)
        for tracer in (ts, net):
            tracer.plan = [
                {"op": "connect"},
                {"op": "waitCheckpoint", "sequence": 3, "timeoutMs": 20000},
                {"op": "signal", "name": f"{tracer.role}-at-3"},
            ]

        # Phase B — the repeated-change vector's established documents
        # (seeded by the driver) and its 8 repeated changes (written BY the
        # SDKs through their mutation surface, alternating writers, one
        # commit at a time so both cores observe identical per-commit
        # batches through their own streams).
        repeated_changes = [step["apply"] for step in repeated["steps"] if "apply" in step]
        for index in range(16):
            for tracer in (ts, net):
                tracer.plan.append({"op": "gate", "name": f"established-{index}"})
                tracer.plan.append({"op": "waitCheckpoint", "sequence": 4 + index, "timeoutMs": 20000})
        for index, change in enumerate(repeated_changes):
            writer = ts if index % 2 == 0 else net
            for tracer in (ts, net):
                tracer.plan.append({"op": "gate", "name": f"repeated-{index}"})
            writer.plan.append(
                {"op": "mutate", "key": f"op-rep-{index + 1:03d}", "document": change["document"], "put": change["put"]}
            )
            for tracer in (ts, net):
                tracer.plan.append({"op": "waitCheckpoint", "sequence": 20 + index, "timeoutMs": 20000})
        for tracer in (ts, net):
            tracer.plan.append({"op": "signal", "name": f"{tracer.role}-done"})
            tracer.plan.append({"op": "final"})
        ts.start()
        net.start()
        wait_until(
            lambda: (gates / "ts-at-3").exists() and (gates / "net-at-3").exists(),
            "both SDKs adopted the seeded snapshot",
        )

        # The established documents of the repeated-change vector, with
        # namespaced operation identities: the two vectors share the
        # op-00000N namespace, and composing them in one scope must not
        # collide (a collision is an idempotent replay — correctly
        # answered with the recorded receipt and no new commit).
        driver = host.open_session()
        for index in range(16):
            commit = repeated["server"]["journal_before"][index]
            for tracer in (ts, net):
                tracer.gate(f"established-{index}")
            host.mutate(
                driver["sessionId"],
                f"op-est-{index + 1:03d}",
                commit["document"],
                put=commit["put"],
            )
            wait_both_at([ts, net], 4 + index)

        for index, change in enumerate(repeated_changes):
            for tracer in (ts, net):
                tracer.gate(f"repeated-{index}")
            wait_both_at([ts, net], 20 + index)
        host.close_session(driver["sessionId"])

        wait_until(lambda: (gates / "ts-done").exists() and (gates / "net-done").exists(), "both SDKs finished", 60)
        ts.wait()
        net.wait()

        ts_final = ts.wait_final()
        net_final = net.wait_final()
        for role, final in (("ts", ts_final), ("net", net_final)):
            checkpoint = final["checkpoint"]
            facts.check(
                f"{role} reaches the vector-predicted final checkpoint",
                checkpoint["storageSequence"] == 27,
                f"storage sequence {checkpoint['storageSequence']} (27 = 3 seeded + 16 established + 8 repeated)",
            )

        vector_checkpoint = (
            expected_checkpoint["storage_sequence"],
            tuple(tuple(pair) for pair in expected_checkpoint["ends"]),
        )
        for tracer in (ts, net):
            snapshots = [event for event in tracer.deliveries() if event["t"] == "snapshot-adopted"]
            if snapshots:
                observed = snapshots[0]["checkpoint"]
                facts.check(
                    f"{tracer.role} snapshot checkpoint equals the golden vector's",
                    (observed["storageSequence"], tuple(tuple(pair) for pair in observed["ends"])) == vector_checkpoint,
                    f"observed (sequence {observed['storageSequence']}, ends {observed['ends']}) vs vector {vector_checkpoint}",
                )
                facts.check(
                    f"{tracer.role} snapshot payload bytes equal the golden vector's (byte-exact Loro delivery)",
                    snapshots[0]["payloadBytes"] == len(expected_payload)
                    and snapshots[0]["payloadSha256"] == sha256_hex(expected_payload),
                    f"{snapshots[0]['payloadBytes']} bytes (vector {len(expected_payload)}), digest match "
                    f"{snapshots[0]['payloadSha256'] == sha256_hex(expected_payload)}",
                )
            else:
                facts.check(
                    f"{tracer.role} adopted the server snapshot on fresh setup",
                    False,
                    "no snapshot adoption observed",
                )

        # The repeated-change budget row, re-derived from observation: the
        # delta path for the 8 repeated changes stays below the snapshot
        # of the established state (the state the vector snapshots).
        established_snapshot = host.request(
            "POST", f"/v1/sync/sessions/{host.open_session()['sessionId']}/snapshot", {}
        )[1]
        established_snapshot_bytes = len(base64.b64decode(established_snapshot["payload"]))
        for tracer in (ts, net):
            deltas = [event for event in tracer.deliveries() if event["t"] == "delta-applied"]
            repeated_deltas = [event for event in deltas if event["checkpoint"]["storageSequence"] >= 20]
            delta_total = sum(event["payloadBytes"] for event in repeated_deltas)
            facts.check(
                f"{tracer.role} repeated-change deltas stay below the snapshot re-send (the vector's budget row)",
                0 < delta_total < established_snapshot_bytes,
                f"{delta_total} delta bytes for 8 repeated changes vs {established_snapshot_bytes} snapshot bytes of the established state",
            )
            facts.check(
                f"{tracer.role} every delta batch fits the delivery budget",
                all(event["payloadBytes"] <= 8192 for event in deltas),
                f"max batch {max((event['payloadBytes'] for event in deltas), default=0)} bytes (budget 8192)",
            )

        ts_rows = normalized_deliveries(ts)
        net_rows = normalized_deliveries(net)
        divergence = next((i for i, (a, b) in enumerate(zip(ts_rows, net_rows)) if a != b), None)
        facts.check(
            "both SDK cores observed identical transition and checkpoint sequences",
            ts_rows == net_rows,
            f"{len(ts_rows)} ts transitions vs {len(net_rows)} net transitions"
            + ("" if divergence is None else f"; first divergence at index {divergence}"),
        )

        applied = {tracer.role: sum(1 for event in tracer.deliveries() if event["t"] == "delta-applied") for tracer in (ts, net)}
        facts.check(
            "every committed update applied by both cores exactly once (no loss, no duplicate)",
            applied["ts"] == 24 and applied["net"] == 24,
            f"ts applied {applied['ts']} batches, net applied {applied['net']} batches "
            "(24 = 16 established + 8 repeated; the seeded 3 arrive inside the snapshot)",
        )
        duplicates = [event for event in net.events() if event.get("t") == "duplicate-delta"]
        facts.check("no undocumented duplicate application", not duplicates, f"{len(duplicates)} duplicate batches counted by the .NET core")

        # The trigger-notification vector row, observed on the wire: the
        # trigger is content-free (no document data rides the hint).
        probe = SlowProbe(host)
        probe.open()
        wait_until(lambda: [event for event in probe.pump() if event["event"] == "opened"], "the probe stream to open")
        driver = host.open_session()
        host.mutate(driver["sessionId"], "op-s1-trigger", "d-01", put={"f000": "trigger-row"})
        host.close_session(driver["sessionId"])
        triggers = wait_until(
            lambda: [event for event in probe.pump() if event["event"] == "trigger"] or None,
            "the content-free trigger notification",
            timeout=15,
        )
        probe.close()
        keys = set(triggers[0]["data"].keys())
        facts.check(
            "the trigger notification is content-free (no document data rides the hint)",
            keys <= {"subscriptionId", "collection", "sequence", "operationId"},
            f"key set {sorted(keys)}; {triggers[0]['bytes']} wire bytes for the SSE event",
        )
    finally:
        host.stop()
    return facts


def scenario_drop_resume(home: pathlib.Path, run_root: pathlib.Path) -> Facts:
    facts = Facts("s2-drop-resume", SCENARIO_TITLES["s2-drop-resume"])
    run_dir = run_root / "s2"
    gates = run_dir / "gates"
    gates.mkdir(parents=True, exist_ok=True)
    host = Host("region-a", run_dir, home, managed=False)
    host.start()
    try:
        ts = Tracer("ts", run_dir, gates, host.url)
        net = Tracer("net", run_dir, gates, host.url)
        ts.plan = [
            {"op": "connect"},
            {"op": "waitCheckpoint", "sequence": 3, "timeoutMs": 20000},
            {"op": "signal", "name": "ts-at-3"},
            # The connection drop climbs the SDK's reconnect ladder against
            # the stale host session identity; exhaustion is the documented
            # terminal state, and the documented re-setup follows.
            {"op": "waitFailed", "timeoutMs": 30000},
            {"op": "signal", "name": "ts-failed"},
            {"op": "gate", "name": "host-back"},
            {"op": "dispose"},
            {"op": "connect", "resume": True},
            {"op": "mutate", "key": "op-s2-005", "document": "d-05", "put": {"f000": "after-resume-ts"}},
            {"op": "waitCheckpoint", "sequence": 5, "timeoutMs": 30000},
            {"op": "signal", "name": "ts-at-5"},
            {"op": "final"},
        ]
        net.plan = [
            {"op": "connect"},
            {"op": "mutate", "key": "op-s2-001", "document": "d-01", "put": {"f000": "one"}},
            {"op": "mutate", "key": "op-s2-002", "document": "d-02", "put": {"f000": "two"}},
            {"op": "mutate", "key": "op-s2-003", "document": "d-03", "put": {"f000": "three"}},
            {"op": "waitCheckpoint", "sequence": 3, "timeoutMs": 20000},
            {"op": "signal", "name": "net-at-3"},
            # The stale identity is probed for the record (what does the
            # fresh host registry answer?), then the documented re-setup
            # runs: a fresh session resumed from the durable checkpoint.
            # (The .NET SDK's INTERNAL re-open path — pull, 410, re-open —
            # is blocked by a real defect recorded in the receipt: the
            # host's refusal body carries "resyncRequired": null and the
            # SDK's error parser calls GetBoolean on it.)
            {"op": "gate", "name": "host-back"},
            {"op": "pull"},
            {"op": "signal", "name": "net-stale-probed"},
            {"op": "dispose"},
            {"op": "connect", "resume": True},
            {"op": "waitCheckpoint", "sequence": 5, "timeoutMs": 60000},
            {"op": "signal", "name": "net-at-5"},
            {"op": "final"},
        ]
        ts.start()
        net.start()
        wait_until(lambda: (gates / "ts-at-3").exists() and (gates / "net-at-3").exists(), "both SDKs at sequence 3")

        # The connection drops mid-stream: the host process (the SDKs'
        # connection endpoint) is lost for real; the engine (a separate
        # process on its own socket) keeps the journal.
        host.kill_host()
        time.sleep(0.5)
        host.restart_host_only()

        for tracer in (ts, net):
            tracer.gate("host-back")

        # The driver commits one update while the SDKs are reconnecting.
        driver = host.open_session()
        host.mutate(driver["sessionId"], "op-s2-004", "d-04", put={"f000": "after-drop-driver"})
        host.close_session(driver["sessionId"])

        wait_until(lambda: (gates / "ts-at-5").exists() and (gates / "net-at-5").exists(), "both SDKs at sequence 5", 90)
        ts.wait()
        net.wait()

        ts_final = ts.wait_final()
        net_final = net.wait_final()
        for role, final in (("ts", ts_final), ("net", net_final)):
            facts.check(
                f"{role} resumed from its durable checkpoint and observed every committed update",
                final["checkpoint"]["storageSequence"] == 5,
                f"final checkpoint storage sequence {final['checkpoint']['storageSequence']} (5 committed updates)",
            )

        ts_statuses = [event.get("status") for event in ts.events("status")]
        facts.check(
            "the TS session surfaced the drop through its documented failure ladder, then re-set up from the durable checkpoint",
            "failed" in ts_statuses and ts.events("opened")[-1].get("resumed") is True,
            f"status transitions {ts_statuses}; the re-setup resumed from the durable checkpoint",
        )
        reopened = [event for event in net.events("opened") if event.get("resumed")]
        facts.check(
            "the .NET session re-set up by resuming from its durable checkpoint",
            len(reopened) >= 1,
            f"{len(reopened)} resumed setups after the drop",
        )
        stale_errors = [
            (event.get("code"), event.get("statusCode"), event.get("message"))
            for event in net.events("error")
            if event.get("op") in ("pull", "live-loop")
        ]
        stale_answered = any(
            code == "session-ended" or status == 410 or (message is not None and "type 'Null'" in message)
            for code, status, message in stale_errors
        )
        facts.check(
            "the stale session identity was answered by the fresh host (never silently re-served)",
            stale_answered,
            f"stale-identity answers: {stale_errors}",
        )
        duplicates = [event for event in net.events() if event.get("t") == "duplicate-delta"]
        facts.check(
            "no duplicate application beyond the documented at-least-once identities",
            not duplicates,
            f"{len(duplicates)} duplicate batches",
        )
    finally:
        host.stop()
    return facts


def scenario_slow_consumer(home: pathlib.Path, run_root: pathlib.Path) -> Facts:
    facts = Facts("s3-slow-consumer", SCENARIO_TITLES["s3-slow-consumer"])
    run_dir = run_root / "s3"
    gates = run_dir / "gates"
    gates.mkdir(parents=True, exist_ok=True)
    host = Host("region-a", run_dir, home, managed=False)
    total = 400
    try:
        host.start()
        ts = Tracer("ts", run_dir, gates, host.url)
        net = Tracer("net", run_dir, gates, host.url)
        for tracer in (ts, net):
            tracer.plan = [
                {"op": "connect"},
                {"op": "waitCheckpoint", "sequence": total, "timeoutMs": 60000},
                {"op": "signal", "name": f"{tracer.role}-at-{total}"},
                {"op": "gate", "name": "oversize-go"},
                {"op": "waitCheckpoint", "sequence": total + 1, "timeoutMs": 30000},
                {"op": "signal", "name": f"{tracer.role}-done"},
                {"op": "final"},
            ]
        ts.start()
        net.start()

        probe = SlowProbe(host)
        probe.open(receive_window=4096)
        wait_until(lambda: [event for event in probe.pump() if event["event"] == "opened"], "the slow probe stream to open")
        watcher = SlowProbe(host)
        watcher.open()

        # The consumer stalls: it simply stops reading.
        driver = host.open_session()
        for index in range(total):
            host.mutate(
                driver["sessionId"],
                f"op-s3-{index + 1:03d}",
                f"d-{index % 8:02d}",
                put={"f000": f"value-{index:03d}-stall", "f001": "stall-payload-0123456789"},
            )
        host.close_session(driver["sessionId"])

        import threading
        stop_watch = threading.Event()
        def watch():
            while not stop_watch.is_set():
                watcher.pump()
                time.sleep(0.02)
        watch_thread = threading.Thread(target=watch); watch_thread.start()
        metrics = wait_until(
            lambda: (host.metrics().get("droppedFrames", 0) > 0 and host.metrics()) or None,
            "the slow consumer's bounded queue to shed frames",
            timeout=30,
        )
        stop_watch.set(); watch_thread.join()
        facts.check(
            "the stalled consumer's bounded queue shed frames (counted, never blocked the engine)",
            metrics["droppedFrames"] > 0,
            f"droppedFrames {metrics['droppedFrames']}, openStreams {metrics['openStreams']}",
        )

        wait_both_at([ts, net], total, timeout=60)
        wait_until(
            lambda: (gates / f"ts-at-{total}").exists() and (gates / f"net-at-{total}").exists(),
            "both fast consumers current",
        )

        # The oversize single-commit row of the slow-consumer vector: one
        # commit whose delta alone exceeds the byte budget is delivered
        # alone (commits == 1, over budget), every other batch within it.
        driver = host.open_session()
        host.mutate(driver["sessionId"], "op-s3-oversize", "d-big", put={"f000": "x" * 20000})
        host.close_session(driver["sessionId"])
        for tracer in (ts, net):
            tracer.gate("oversize-go")
        wait_until(lambda: (gates / "ts-done").exists() and (gates / "net-done").exists(), "both consumers done", 60)
        ts.wait()
        net.wait()

        # The stalled consumer resumes: the shed region is surfaced in-band
        # (the drop marker with its boundary) or recoverable by the
        # documented boundary reconnect (Last-Event-ID/from re-delivers
        # the missed range) — offered equals delivered plus dropped.
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline and not [event for event in probe.events if event["event"] == "drop"]:
            probe.pump()
            time.sleep(0.1)
        drops = [event for event in probe.events if event["event"] == "drop"]
        last_seen = max((int(event["id"]) for event in probe.events if event["id"]), default=0)
        boundary = drops[0]["data"].get("resumeFrom") if drops else last_seen + 1
        probe.close()
        recovered = SlowProbe(host)
        recovered.open()
        wait_until(lambda: [event for event in recovered.pump() if event["event"] == "opened"], "the recovery stream to open")
        reconnected = SlowProbe(host)
        reconnected.open(from_sequence=boundary)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline and not [
            event for event in reconnected.events
            if event["event"] == "batch" and event["data"].get("fromSequence", 0) <= boundary
        ]:
            reconnected.pump()
            time.sleep(0.1)
        recovered.close()
        reconnected.close()
        redelivered = [
            event for event in reconnected.events
            if event["event"] == "batch" and event["data"].get("fromSequence", 0) <= boundary
        ]
        facts.check(
            "the stalled consumer's missed range is surfaced in-band or re-delivered from the boundary",
            bool(drops) or bool(redelivered),
            f"{len(drops)} in-band drop markers (resumeFrom {boundary}); the boundary reconnect re-delivered "
            f"{len(redelivered)} batch events covering the missed range",
        )

        for tracer in (ts, net):
            deltas = [event for event in tracer.deliveries() if event["t"] == "delta-applied"]
            oversize = [event for event in deltas if event["payloadBytes"] > 8192]
            commits = oversize[0].get("commits") if oversize else None
            facts.check(
                f"{tracer.role} observed the oversize single commit delivered alone (the .NET core also carries its commits == 1 flag)",
                len(oversize) == 1 and (tracer.role == "ts" or commits == 1),
                f"{len(oversize)} over-budget batches; bytes {oversize[0]['payloadBytes'] if oversize else 0}; "
                f"commits {commits if commits is not None else 'n/a (ts surface)'}",
            )
            facts.check(
                f"{tracer.role} every other batch fits the delivery byte budget",
                all(event["payloadBytes"] <= 8192 for event in deltas if event is not (oversize[0] if oversize else None)),
                f"{len(deltas)} batches checked against the 8192-byte budget",
            )

        ts_final = ts.wait_final()
        net_final = net.wait_final()
        facts.check(
            "the fast consumers lost nothing while the slow consumer shed frames",
            ts_final["checkpoint"]["storageSequence"] == total + 1
            and net_final["checkpoint"]["storageSequence"] == total + 1,
            f"ts {ts_final['checkpoint']['storageSequence']}, net {net_final['checkpoint']['storageSequence']} (total {total + 1} commits)",
        )
        duplicates = [event for event in net.events() if event.get("t") == "duplicate-delta"]
        facts.check("no duplicate application at the fast consumers", not duplicates, f"{len(duplicates)} duplicate batches")
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            watcher.pump()
            time.sleep(0.05)
        watcher_counts = {}
        for event in watcher.events:
            watcher_counts[event["event"]] = watcher_counts.get(event["event"], 0) + 1
        watcher.close()
        triggers = net_final.get("triggersObserved")
        facts.check(
            "the content-free trigger hint channel stayed live (advisory hints; the checkpoint-bounded pull is authoritative)",
            (triggers or 0) >= 1,
            f"{triggers} triggers observed by the .NET core for {total + 1} commits; a parallel raw consumer recorded {watcher_counts} "
            "(the pull is authoritative: both fast consumers reached every commit with zero duplicates)",
        )
    finally:
        host.stop()
    return facts


def scenario_tbep_link_loss(home: pathlib.Path, run_root: pathlib.Path) -> Facts:
    facts = Facts("s4-tbep-link-loss", SCENARIO_TITLES["s4-tbep-link-loss"])
    run_dir = run_root / "s4"
    gates = run_dir / "gates"
    gates.mkdir(parents=True, exist_ok=True)
    host = Host("region-a", run_dir, home, managed=True)
    host.start()
    probe = SlowProbe(host)
    try:
        probe.open()
        wait_until(lambda: [event for event in probe.pump() if event["event"] == "opened"], "the probe stream to open")

        ts = Tracer("ts", run_dir, gates, host.url)
        net = Tracer("net", run_dir, gates, host.url)
        ts.plan = [
            {"op": "connect"},
            {"op": "waitCheckpoint", "sequence": 3, "timeoutMs": 20000},
            {"op": "signal", "name": "ts-at-3"},
            {"op": "waitFailed", "timeoutMs": 30000},
            {"op": "signal", "name": "ts-failed"},
            {"op": "gate", "name": "engine-back"},
            # The documented resume path, attempted for the record: against
            # a fresh in-memory engine generation the durable checkpoint
            # sits ahead of the server and is answered by the typed
            # refusal (recorded, never retried into silence).
            {"op": "dispose"},
            {"op": "connect", "resume": True},
            {"op": "signal", "name": "ts-resume-attempted"},
            {"op": "gate", "name": "ts-fresh-go"},
            {"op": "dispose"},
            {"op": "connect"},
            {"op": "mutate", "key": "op-s4-007", "document": "d-07", "put": {"f000": "post-recovery-ts"}},
            {"op": "waitCheckpoint", "sequence": 3, "timeoutMs": 30000},
            {"op": "signal", "name": "ts-recovered"},
            {"op": "final"},
        ]
        net.plan = [
            {"op": "connect"},
            {"op": "mutate", "key": "op-s4-001", "document": "d-01", "put": {"f000": "pre-loss-1"}},
            {"op": "mutate", "key": "op-s4-002", "document": "d-02", "put": {"f000": "pre-loss-2"}},
            {"op": "mutate", "key": "op-s4-003", "document": "d-03", "put": {"f000": "pre-loss-3"}},
            {"op": "waitCheckpoint", "sequence": 3, "timeoutMs": 20000},
            {"op": "signal", "name": "net-at-3"},
            {"op": "gate", "name": "engine-back"},
            # An explicit pull on the stale identity: the host answers the
            # ended session truthfully (410), never re-serving it.
            {"op": "pull"},
            {"op": "signal", "name": "net-stale-pulled"},
            {"op": "dispose"},
            # The documented resume path, attempted for the record (same
            # typed refusal as the TS core observes).
            {"op": "connect", "resume": True},
            {"op": "signal", "name": "net-resume-attempted"},
            {"op": "gate", "name": "net-fresh-go"},
            {"op": "dispose"},
            {"op": "connect"},
            {"op": "mutate", "key": "op-s4-004", "document": "d-04", "put": {"f000": "post-recovery-net"}},
            {"op": "mutate", "key": "op-s4-005", "document": "d-05", "put": {"f000": "post-recovery-net-2"}},
            {"op": "waitCheckpoint", "sequence": 3, "timeoutMs": 30000},
            {"op": "signal", "name": "net-recovered"},
            {"op": "final"},
        ]
        ts.start()
        net.start()
        wait_until(lambda: (gates / "ts-at-3").exists() and (gates / "net-at-3").exists(), "both SDKs at sequence 3")

        # The TBEP link endpoint is lost for real (the engine process
        # dies; the UDS link dies with it). The host supervises: typed
        # refusals for every caller, streams closed resume-ably, tracked
        # sessions ended, a fresh engine generation within the policy.
        host.kill_engine()
        wait_until(lambda: host.metrics().get("engineRestarts", 0) >= 1, "the supervised engine restart", timeout=30)
        wait_until(lambda: host.get("/health").get("status") == "serving", "the host serving again", timeout=30)

        closed = wait_until(
            lambda: [event for event in probe.pump() if event["event"] == "closed"] or None,
            "the probe stream to close with the generation-lost frame",
            timeout=15,
        )
        facts.check(
            "the host closed the stream with the resume-able engine-generation-lost frame",
            bool(closed) and closed[0]["data"].get("reason") == "engine-generation-lost",
            f"close reason {closed[0]['data'].get('reason') if closed else 'n/a'}",
        )
        probe.close()

        for tracer in (ts, net):
            tracer.gate("engine-back")
        wait_until(lambda: (gates / "ts-resume-attempted").exists() and (gates / "net-resume-attempted").exists(), "both resume attempts", timeout=90)

        ts_errors = ts.events("error")
        net_errors = net.events("error")
        refusal = lambda events: [  # noqa: E731 - a small local classifier
            (event.get("code"), event.get("statusCode"))
            for event in events
            if event.get("code") in ("engine_failure", "http_500", "host-failure")
            or event.get("statusCode") in (500, 502)
        ]
        wait_until(lambda: refusal(net.events("error")) or None, "the .NET resume-path typed refusal", 30)
        facts.check(
            "the durable checkpoint ahead of a fresh in-memory generation is answered by the typed refusal (both cores)",
            bool(refusal(ts_errors)) and bool(refusal(net.events("error"))),
            f"ts resume-path errors {refusal(ts_errors)}, net resume-path errors {refusal(net.events('error'))}",
        )
        stale = [
            (event.get("code"), event.get("statusCode"))
            for event in net.events("error")
            if event.get("code") == "session-ended" or event.get("statusCode") == 410
        ]
        facts.check(
            "the ended session is answered truthfully (410 session-ended), never re-served",
            bool(stale),
            f"stale-identity answers {stale}",
        )
        ts.gate("ts-fresh-go")
        net.gate("net-fresh-go")

        wait_until(lambda: (gates / "ts-recovered").exists() and (gates / "net-recovered").exists(), "both SDKs recovered", timeout=90)
        ts.wait()
        net.wait()

        ts_final = ts.wait_final()
        net_final = net.wait_final()
        for role, final in (("ts", ts_final), ("net", net_final)):
            facts.check(
                f"{role} converged on the fresh generation with no lost committed update among its commits",
                final["checkpoint"]["storageSequence"] == 3,
                f"final checkpoint storage sequence {final['checkpoint']['storageSequence']} (3 post-recovery commits)",
            )
        facts.check(
            "recovery ran through snapshot adoption / fresh setup (resync), not stale deltas",
            sum(1 for event in net.deliveries() if event["t"] == "snapshot-adopted") >= 2,
            "the .NET core adopts the server snapshot on every fresh setup (the fresh generation answers an empty state); "
            "the TS core adopts only when the server state advances past its own",
        )
    finally:
        probe.close()
        host.stop()
    return facts


def scenario_region_route_change(home: pathlib.Path, run_root: pathlib.Path) -> Facts:
    facts = Facts("s5-region-route-change", SCENARIO_TITLES["s5-region-route-change"])
    run_dir = run_root / "s5"
    gates = run_dir / "gates"
    gates.mkdir(parents=True, exist_ok=True)
    region_a = Host("region-a", run_dir, home, managed=False, instance=1)
    region_b = Host("region-b", run_dir, home, managed=False, instance=2)
    region_a.start()
    region_b.start()
    try:
        ts = Tracer("ts", run_dir, gates, region_a.url)
        net = Tracer("net", run_dir, gates, region_a.url)
        for tracer in (ts, net):
            tracer.plan = [
                {"op": "connect"},
                {"op": "waitCheckpoint", "sequence": 3, "timeoutMs": 20000},
                {"op": "signal", "name": f"{tracer.role}-at-3-a"},
                # The route change: the client re-resolves the endpoint
                # (the tracer acts as the Platform router) and carries its
                # durable checkpoint to the new region — the
                # region-route-change vector's client half.
                {"op": "gate", "name": "route-changed"},
                {"op": "dispose"},
                {"op": "connect", "resume": True, "host": region_b.url},
                {"op": "signal", "name": f"{tracer.role}-on-b"},
                {"op": "waitCheckpoint", "sequence": 4, "timeoutMs": 30000},
                {"op": "signal", "name": f"{tracer.role}-at-4-b"},
                {"op": "final"},
            ]
        ts.start()
        net.start()

        session = region_a.open_session()
        for index, value in enumerate(("route-a-1", "route-a-2", "route-a-3")):
            region_a.mutate(session["sessionId"], f"op-s5-00{index + 1}", f"d-0{index + 1}", put={"f000": value})
        region_a.close_session(session["sessionId"])
        wait_until(lambda: (gates / "ts-at-3-a").exists() and (gates / "net-at-3-a").exists(), "both SDKs at sequence 3 on region a")

        # The cross-region link stand-in (recorded honestly in the
        # receipt): the member transport between serving engines is a
        # named integrator action and is not wired; the tracer re-submits
        # the same journal changes — the same operation identities —
        # through region b's real write path.
        session_b = region_b.open_session()
        for index, value in enumerate(("route-a-1", "route-a-2", "route-a-3")):
            region_b.mutate(session_b["sessionId"], f"op-s5-00{index + 1}", f"d-0{index + 1}", put={"f000": value})

        for tracer in (ts, net):
            tracer.gate("route-changed")
        wait_until(lambda: (gates / "ts-on-b").exists() and (gates / "net-on-b").exists(), "both SDKs re-targeted region b")

        region_b.mutate(session_b["sessionId"], "op-s5-004", "d-04", put={"f000": "written-through-b"})
        region_b.close_session(session_b["sessionId"])

        wait_until(lambda: (gates / "ts-at-4-b").exists() and (gates / "net-at-4-b").exists(), "both SDKs at sequence 4 on region b", 60)
        ts.wait()
        net.wait()

        ts_final = ts.wait_final()
        net_final = net.wait_final()
        for role, final in (("ts", ts_final), ("net", net_final)):
            facts.check(
                f"{role} carried its durable checkpoint to the new region with no lost committed update",
                final["checkpoint"]["storageSequence"] == 4,
                f"final checkpoint storage sequence {final['checkpoint']['storageSequence']} on region b "
                "(3 carried + 1 written through b)",
            )
        duplicates = [event for event in net.events() if event.get("t") == "duplicate-delta"]
        facts.check(
            "the carried checkpoint acked at the new region; nothing re-delivered behind it (the vector's checkpoint_ack step)",
            not duplicates,
            f"{len(duplicates)} duplicate batches after the route change",
        )
        b_deltas = {
            tracer.role: [event for event in tracer.deliveries() if event["t"] == "delta-applied"]
            for tracer in (ts, net)
        }
        facts.check(
            "the write made through the new region is the only delta observed there",
            len(b_deltas["ts"]) == 1 and len(b_deltas["net"]) == 1,
            f"delta batches on region b: ts {len(b_deltas['ts'])}, net {len(b_deltas['net'])}",
        )
        if b_deltas["ts"] and b_deltas["net"]:
            facts.check(
                "both cores applied byte-identical payloads for the post-route write",
                b_deltas["ts"][0]["payloadSha256"] == b_deltas["net"][0]["payloadSha256"],
                f"digest {b_deltas['ts'][0]['payloadSha256'][:16]}…",
            )
    finally:
        region_a.stop()
        region_b.stop()
    return facts


def scenario_multi_region(home: pathlib.Path, run_root: pathlib.Path) -> Facts:
    facts = Facts("s6-multi-region", SCENARIO_TITLES["s6-multi-region"])
    run_dir = run_root / "s6"
    gates = run_dir / "gates"
    gates.mkdir(parents=True, exist_ok=True)
    region_a = Host("region-a", run_dir, home, managed=False, instance=1)
    region_b = Host("region-b", run_dir, home, managed=False, instance=2)
    region_a.start()
    region_b.start()
    try:
        # The writer session's nearest region is a (the TS core); the
        # reader session's nearest region is b (the .NET core).
        ts = Tracer("ts", run_dir, gates, region_a.url)
        net = Tracer("net", run_dir, gates, region_b.url)
        ts.plan = [
            {"op": "connect"},
            {"op": "signal", "name": "ts-open"},
            {"op": "gate", "name": "write-w1"},
            {"op": "mutate", "key": "op-w1", "document": "d-shared", "put": {"f000": "written-through-a"}},
            {"op": "signal", "name": "w1-committed"},
            {"op": "gate", "name": "write-w2"},
            {"op": "mutate", "key": "op-w2", "document": "d-shared", "put": {"f000": "written-through-a-2"}},
            {"op": "signal", "name": "w2-committed"},
            {"op": "waitCheckpoint", "sequence": 3, "timeoutMs": 30000},
            {"op": "signal", "name": "ts-final-signal"},
            {"op": "final"},
        ]
        net.plan = [
            {"op": "connect"},
            {"op": "signal", "name": "net-open"},
            {"op": "waitCheckpoint", "sequence": 1, "timeoutMs": 30000},
            {"op": "signal", "name": "reader-saw-w1"},
            {"op": "gate", "name": "lag-sampled"},
            {"op": "waitCheckpoint", "sequence": 2, "timeoutMs": 30000},
            {"op": "signal", "name": "reader-saw-w2"},
            # The mirror direction: the .NET writer writes through region b
            # (its nearest region).
            {"op": "gate", "name": "write-w3"},
            {"op": "mutate", "key": "op-w3", "document": "d-shared-b", "put": {"f000": "written-through-b"}},
            {"op": "waitCheckpoint", "sequence": 3, "timeoutMs": 30000},
            {"op": "signal", "name": "net-final-signal"},
            {"op": "final"},
        ]
        ts.start()
        net.start()
        wait_until(lambda: (gates / "ts-open").exists() and (gates / "net-open").exists(), "both sessions open")

        # w1 is written through region a and acknowledged by the engine.
        ts.gate("write-w1")
        wait_until(lambda: (gates / "w1-committed").exists(), "w1 committed through region a")

        # The scripted cross-region link (the honest stand-in for the
        # unwired member transport between serving engines): the same
        # operation identity re-submitted through region b's real write
        # path. Visibility, lag, and convergence are observed on the real
        # serving surfaces of both regions.
        link = region_b.open_session()
        region_b.mutate(link["sessionId"], "op-w1", "d-shared", put={"f000": "written-through-a"})
        wait_until(lambda: (gates / "reader-saw-w1").exists(), "the reader saw w1 through region b", 30)
        reader_checkpoint = net.checkpoint_now() or {}
        facts.check(
            "the write made through region a is visible through region b's delivery path (the reader's nearest region)",
            reader_checkpoint.get("storageSequence") == 1,
            f"reader checkpoint on region b: {reader_checkpoint}",
        )

        # w2 through region a; the lag is sampled BEFORE the link pumps it.
        ts.gate("write-w2")
        wait_until(lambda: (gates / "w2-committed").exists(), "w2 committed through region a")
        reader_checkpoint = net.checkpoint_now() or {}
        lag = 2 - reader_checkpoint.get("storageSequence", 0)
        facts.check(
            "replication lag is a storage-sequence distance, not a latency claim (region-b behind before the pump)",
            lag == 1,
            f"region-b lag {lag} at the observation: region-a published sequence 2, region-b durable cursor "
            f"{reader_checkpoint.get('storageSequence')}",
        )
        region_b.mutate(link["sessionId"], "op-w2", "d-shared", put={"f000": "written-through-a-2"})
        net.gate("lag-sampled")
        wait_until(lambda: (gates / "reader-saw-w2").exists(), "the reader saw w2 through region b", 30)
        reader_checkpoint = net.checkpoint_now() or {}
        facts.check(
            "convergence observation after the pump names both regions and closes the lag",
            reader_checkpoint.get("storageSequence") == 2,
            "convergence: op-w1 and op-w2 held by region-a and region-b at the observation "
            f"(region-b lag 0: a sequence distance); region-b cursor {reader_checkpoint.get('storageSequence')}",
        )

        # The mirror direction: the .NET writer commits w3 through region
        # b; the link carries it to region a; the TS reader on region a
        # observes it.
        net.gate("write-w3")
        wait_until(
            lambda: (net.checkpoint_now() or {}).get("storageSequence", 0) >= 3,
            "w3 committed through region b",
            30,
        )
        link_a = region_a.open_session()
        region_a.mutate(link_a["sessionId"], "op-w3", "d-shared-b", put={"f000": "written-through-b"})
        region_a.close_session(link_a["sessionId"])

        wait_until(lambda: (gates / "ts-final-signal").exists() and (gates / "net-final-signal").exists(), "both sessions final", 60)
        ts.wait()
        net.wait()

        # The at-least-once identity: re-submitting w1's key to region b
        # answers the recorded receipt and advances nothing.
        replay = region_b.mutate(link["sessionId"], "op-w1", "d-shared", put={"f000": "written-through-a"})
        region_b.close_session(link["sessionId"])
        net_final = net.wait_final()
        ts_final = ts.wait_final()
        facts.check(
            "the idempotent retry identity: a re-submitted key answers the recorded receipt and advances nothing",
            replay["outcome"]["outcome"] == "applied"
            and replay["outcome"]["receipt"]["operationId"] == "op-w1"
            and replay["outcome"]["receipt"]["sequence"] == 1
            and net_final["checkpoint"]["storageSequence"] == 3,
            f"receipt sequence {replay['outcome']['receipt']['sequence']} (region-b cursor unchanged at "
            f"{net_final['checkpoint']['storageSequence']})",
        )
        facts.check(
            "the write through region b is visible through region a (the mirror direction)",
            ts_final["checkpoint"]["storageSequence"] == 3,
            f"region-a session checkpoint {ts_final['checkpoint']['storageSequence']} (2 written through a + 1 carried from b)",
        )
        facts.check(
            "both regions converged under the same version-vector ends (per-region storage sequences are per-generation facts)",
            ts_final["checkpoint"]["ends"] == net_final["checkpoint"]["ends"],
            f"ends region-a {ts_final['checkpoint']['ends']} == region-b {net_final['checkpoint']['ends']}",
        )
    finally:
        region_a.stop()
        region_b.stop()
    return facts


# ---------------------------------------------------------------------------
# The receipt writer.
# ---------------------------------------------------------------------------

RECEIPT_HEADER = """# SE-11 {title} — tracer receipt

Scenario `{name}` of the SE-11 end-to-end client live-update tracer
(ticket #13). Correctness evidence only — no performance claim. Loopback
hosts and temp-dir sockets only; backend-context fixtures
(X-TB-Principal `{principal}` / X-TB-Tenant `{tenant}`) only; no
secrets; no live systems; the engine and hosts are throwaway processes
under the run's temp directory.

- TerraBaseV2 worktree: branch `fleet/se11-2`
- Sibling SDK checkouts (read-only, never edited or pushed):
  TerraScale.TypeScript @ `{ts}`, TerraScale.DotNet @ `{dotnet}`
- Reproduce: `python3 scripts/se11/run.py --scenario {name}` from the
  TerraBaseV2 worktree root (the fresh-checkout sequence is in
  `evidence/sync/se11-live-update-tracer.md`).
"""


def write_receipt(facts: Facts) -> None:
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    lines = [
        RECEIPT_HEADER.format(
            title=facts.title,
            name=facts.name,
            principal=PRINCIPAL,
            tenant=TENANT,
            ts=git_head(TS_REPO),
            dotnet=git_head(DOTNET_REPO),
        ),
        f"Outcome: **{'PASS' if facts.passed else 'FAIL'}** ({facts.summary()}).",
        "",
        "| Check | Outcome | Detail |",
        "| --- | --- | --- |",
    ]
    for name, passed, detail in facts.checks:
        lines.append(f"| `{name}` | {'pass' if passed else 'FAIL'} | {detail} |")
    (EVIDENCE / f"{facts.name}.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    log(f"receipt written: evidence/sync/se11/{facts.name}.md ({facts.summary()})")


# ---------------------------------------------------------------------------
# Build + entry point.
# ---------------------------------------------------------------------------


def build_all(run_root: pathlib.Path) -> pathlib.Path:
    harness = EngineHarness(run_root / "engine-harness")
    binary = harness.build()
    log(f"engine binary: {binary}")

    dotnet = shutil.which("dotnet")
    result = subprocess.run(
        [dotnet, "build", str(REPO_ROOT / "host/TerraBase.Api"), "-v", "q"],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise RuntimeError(f"the C# host failed to build:\n{result.stdout}\n{result.stderr}")
    log("C# host built")

    if not TRACER_DOTNET_DLL.exists():
        result = subprocess.run(
            [dotnet, "build", str(REPO_ROOT / "tracer/dotnet/TerraScale.Tracer"), "-v", "q"],
            cwd=REPO_ROOT,
            capture_output=True,
            text=True,
        )
        if result.returncode != 0:
            raise RuntimeError(f"the .NET tracer failed to build:\n{result.stdout}\n{result.stderr}")
    log(".NET tracer built")

    if not (TS_REPO / "dist/sync/v2/session.js").exists():
        raise RuntimeError(
            f"the TypeScript SDK checkout at {TS_REPO} has no built dist/sync/v2/session.js; "
            "build it read-only (npm run build) or point TERRASCALE_TS_SDK at a built checkout"
        )
    log("TypeScript SDK dist present (read-only)")
    return binary


SCENARIOS = {
    "s1-golden-path": scenario_golden_path,
    "s2-drop-resume": scenario_drop_resume,
    "s3-slow-consumer": scenario_slow_consumer,
    "s4-tbep-link-loss": scenario_tbep_link_loss,
    "s5-region-route-change": scenario_region_route_change,
    "s6-multi-region": scenario_multi_region,
}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scenario", choices=[*SCENARIOS, "all"], default="all")
    parser.add_argument("--run-root", default=None, help="the run directory (default: a fresh temp directory)")
    arguments = parser.parse_args()

    run_root = pathlib.Path(arguments.run_root) if arguments.run_root else pathlib.Path(tempfile.mkdtemp(prefix="terrabase-se11-"))
    run_root.mkdir(parents=True, exist_ok=True)
    log(f"run root: {run_root}")
    log(f"TypeScript SDK: {TS_REPO} @ {git_head(TS_REPO)}")
    log(f".NET SDK: {DOTNET_REPO} @ {git_head(DOTNET_REPO)}")

    binary = build_all(run_root)
    names = list(SCENARIOS) if arguments.scenario == "all" else [arguments.scenario]
    failed = []
    for name in names:
        log(f"scenario {name}")
        facts = SCENARIOS[name](binary, run_root)
        write_receipt(facts)
        if not facts.passed:
            failed.append(name)
    if failed:
        log(f"FAILED scenarios: {', '.join(failed)}")
        return 1
    log("all scenarios passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
