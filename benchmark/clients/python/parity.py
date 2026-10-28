"""SDK/raw HTTPS parity on an owned synthetic tenant; see README.md."""

from __future__ import annotations

import argparse
import asyncio
import contextlib
import hashlib
import json
import math
import os
import platform
import socket
import sys
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from datetime import datetime, timezone
from importlib import metadata
from pathlib import Path
from typing import IO, Any

import httpx

import terrascale
from terrascale import AsyncTerraScale, NotFoundError, is_valid_id, new_id
from terrascale.auth import AsyncAPIKeyTokenSource

from observer import Observer, cell_rejection, pool as _pool
from runtime import MEASURE_S, WARMUP_S, Stats, discover_regions, measure as _measure, run_session

ORIGIN = "https://api.terrascale.tech"
SIZES = (256, 4096)
CONCURRENCY = (1, 16)
LANES = ("raw-h1", "raw-h2", "sdk")
PROTOCOLS = {"raw-h1": "HTTP/1.1", "raw-h2": "HTTP/2"}
COLLECTION = "bench"
READY_S, READY_OP_S, SETUP_S, CLEANUP_S, RUN_S = 60.0, 10.0, 60.0, 30.0, 1800.0


@dataclass(frozen=True)
class Seed:
    id: str
    version: str
    body: dict[str, Any]


Op = Callable[[Seed], Awaitable[None]]


class Rejected(Exception):
    """The run stops. Any evidence row was already written."""


def _check(seed: Seed, raw: Any) -> None:
    if not isinstance(raw, dict) or raw.get("state") != "value" or raw.get("id") != seed.id \
            or raw.get("version") != seed.version or raw.get("document") != seed.body:
        raise ValueError("document identity, version, state or body differs")


def _pct(ordered: list[float], q: float) -> float | None:
    return round(ordered[math.ceil(q * len(ordered)) - 1] * 1000, 3) if ordered else None


async def _ready(op: Op, seed: Seed) -> bool:
    deadline = time.monotonic() + READY_S
    while True:
        try:
            await asyncio.wait_for(op(seed), READY_OP_S)
            return True
        except Exception:  # A new seed may not have reached this region yet. CancelledError still propagates.
            pass
        if time.monotonic() >= deadline:
            return False
        await asyncio.sleep(0.1)


class Tenant:
    def __init__(self, key: str, project: str, db: str, out: IO[str]):
        self.key, self.project, self.db, self.out = key, project, db, out
        self.base = f"/v1/projects/{project}/databases"
        self.db_path = f"{self.base}/{db}"
        self.doc_path = f"{self.db_path}/collections/{COLLECTION}/documents/by-key"
        self.setup_routes = {("POST", self.base), ("DELETE", self.db_path),
                             ("POST", f"{self.db_path}/collections"), ("PUT", self.doc_path)}
        self.seeds: dict[int, Seed] = {}

    def emit(self, row: dict[str, Any]) -> None:
        self.out.write(json.dumps(row, sort_keys=True, separators=(",", ":")) + "\n")
        self.out.flush()

    async def _lane(self, stack: contextlib.AsyncExitStack, lane: str, region: str) -> tuple[Observer, Op]:
        observer = Observer({("GET", self.doc_path)}, region, PROTOCOLS.get(lane))
        pool = await stack.enter_async_context(_pool(observer, lane=lane))
        if lane == "sdk":
            # The SDK's internal token source shares this pool, as shipped.
            collection = AsyncTerraScale(self.key, project=self.project, base_url=ORIGIN,
                                         http_client=pool).database(self.db).collection(COLLECTION)

            async def sdk_get(seed: Seed) -> None:
                _check(seed, (await collection.get(seed.id)).raw)

            return observer, sdk_get
        # Raw lanes take a separate token source on their own pool with the same settings.
        source = AsyncAPIKeyTokenSource(self.key, project=self.project,
                                        http_client=await stack.enter_async_context(_pool()))

        async def raw_get(seed: Seed) -> None:
            token = await source.token()
            response = await pool.get(ORIGIN + self.doc_path, params={"id": seed.id},
                                      headers={"Authorization": f"Bearer {token}", "Accept": "application/json"})
            response.raise_for_status()
            _check(seed, response.json())

        return observer, raw_get

    async def cell(self, region: str, repeat: int, lane: str, size: int, concurrency: int) -> None:
        seed = self.seeds[size]
        async with contextlib.AsyncExitStack() as stack:
            observer, op = await self._lane(stack, lane, region)
            ready = await _ready(op, seed)
            observer.window()
            stats = await _measure(op, seed, concurrency) if ready else Stats()
        latencies = sorted(stats.latencies)
        reject = cell_rejection(ready, stats, observer)
        self.emit({
            "type": "cell", "region": region, "repeat": repeat, "lane": lane, "payload_bytes": size,
            "concurrency": concurrency, "ready": ready, "ok": stats.ok, "sampled": len(latencies),
            "elapsed_s": stats.elapsed_s, "operations_started": stats.started,
            "ops_per_s": round(stats.ok / stats.elapsed_s, 3), "p50_ms": _pct(latencies, 0.50),
            "p95_ms": _pct(latencies, 0.95), "p99_ms": _pct(latencies, 0.99), "attempts": observer.attempts,
            "good_attempts": observer.good, "bad_attempts": observer.attempts - observer.good,
            "protocols": observer.versions, "connects": observer.connects,
            "operation_failures": stats.failed, "stuck": stats.stuck, "reject": reject,
        })
        if reject:
            raise Rejected(f"cell rejected: {reject}")


async def _setup(client: AsyncTerraScale, home: str, db: str) -> dict[int, Seed]:
    handle = client.database(db)
    await client.create_database(db, home_region=home, all_regions=True)
    await handle.create_collection(COLLECTION)
    collection, seeds = handle.collection(COLLECTION), {}
    for size in SIZES:
        body = {"payload": "x" * (size - 14)}  # The compact JSON document is exactly `size` bytes.
        doc_id = new_id()
        written = await collection.put(doc_id, body)
        seeds[size] = Seed(doc_id, written.version, body)
    return seeds


async def _cleanup(client: AsyncTerraScale, db: str, tenant: Tenant) -> bool:
    """Delete the exact owned name directly; the server coordinates remote erasure."""
    row: dict[str, Any] = {"accepted": True}
    try:
        await asyncio.wait_for(client.database(db).delete(), CLEANUP_S)
    except NotFoundError:  # Never created, or already gone.
        pass
    except Exception as exc:
        row = {"accepted": False, "status": getattr(exc, "status", None), "error": type(exc).__name__}
    tenant.emit({"type": "database_deletion", "database": db, "remote_erasure": "unverified", **row})
    return row["accepted"]


def _regions() -> list[str]:
    try:
        return discover_regions()
    except ValueError as exc:
        raise Rejected(str(exc)) from None


def _credentials(path: str) -> tuple[str, str]:
    try:
        with open(path, encoding="utf-8") as handle:
            body = json.load(handle)
        key, project = body["api_key"], body["project"]
    except (OSError, ValueError, KeyError, TypeError):
        raise Rejected("credentials could not be loaded") from None
    if not isinstance(key, str) or not key or not is_valid_id(project):
        raise Rejected("credentials could not be loaded")
    return key, project


def _version(name: str) -> str | None:
    try:
        return metadata.version(name)
    except metadata.PackageNotFoundError:
        return None


def _source() -> str:
    """Hash actual imported SDK source, without logging installation URLs or paths."""
    digest = hashlib.sha256()
    for path in sorted(Path(terrascale.__file__).parent.glob("*.py")):
        digest.update(path.name.encode() + b"\0" + path.read_bytes())
    return digest.hexdigest()


async def _session(args: argparse.Namespace) -> None:
    key, project = _credentials(args.credentials)
    try:
        out = os.fdopen(os.open(args.out, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w", encoding="utf-8")
    except OSError:
        raise Rejected("output must be a new writable file") from None
    with out:
        regions = _regions()
        home, db = regions[0], f"api-perf-{new_id().lower()}"
        tenant = Tenant(key, project, db, out)
        tenant.emit({
            "type": "identity", "started": datetime.now(timezone.utc).isoformat(), "host": socket.gethostname(),
            "python": platform.python_version(), "terrascale": terrascale.__version__, "source": _source(),
            "httpx": httpx.__version__, "httpcore": _version("httpcore"), "h2": _version("h2"),
            "regions": regions, "home": home, "database": db, "warmup_s": WARMUP_S, "window_s": MEASURE_S,
        })
        async with _pool(Observer(tenant.setup_routes, home, measured=False)) as pool:
            client = AsyncTerraScale(key, project=project, base_url=ORIGIN, http_client=pool)
            try:
                try:
                    tenant.seeds = await asyncio.wait_for(_setup(client, home, db), SETUP_S)
                except Exception as exc:  # The safe status and type only, never the message.
                    tenant.emit({"type": "setup_failure", "status": getattr(exc, "status", None),
                                 "error": type(exc).__name__})
                    raise Rejected("setup failed") from None
                for region in regions:
                    for repeat in range(1, 4):
                        for size in SIZES:
                            for concurrency in CONCURRENCY:
                                for lane in (LANES if repeat % 2 else LANES[::-1]):
                                    await tenant.cell(region, repeat, lane, size, concurrency)
            finally:
                cleaned = await _cleanup(client, db, tenant)
    if not cleaned:
        raise Rejected("cleanup failed")


async def _main(args: argparse.Namespace) -> None:
    await run_session(_session(args), RUN_S)


def main() -> int:
    parser = argparse.ArgumentParser(description="Python SDK versus raw HTTPS parity on a synthetic tenant.")
    parser.add_argument("--credentials", required=True, help="private synthetic {api_key, project} JSON file")
    parser.add_argument("--out", required=True, help="new JSONL output file")
    args = parser.parse_args()
    try:
        asyncio.run(_main(args))
    except Rejected as exc:
        print(f"parity rejected: {exc}", file=sys.stderr)
        return 1
    except asyncio.CancelledError:
        print("parity interrupted", file=sys.stderr)
        return 130
    except Exception as exc:  # The type only: messages can echo server text.
        print(f"parity failed: {type(exc).__name__}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
