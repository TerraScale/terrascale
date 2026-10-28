"""Bounded benchmark windows, region discovery and cleanup-safe cancellation."""

from __future__ import annotations

import asyncio
import ipaddress
import os
import random
import signal
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

REGIONS_REQUIRED = ("gru", "sjc", "fra")
WARMUP_S, MEASURE_S, DRAIN_S = 2.0, 5.0, 30.0
Op = Callable[[Any], Awaitable[None]]


@dataclass
class Stats:
    latencies: list[float] = field(default_factory=list)
    started: int = 0  # All warmup/window operations, matching the attempt observer.
    ok: int = 0
    elapsed_s: float = MEASURE_S
    failed: int = 0
    stuck: bool = False


async def measure(op: Op, seed: Any, concurrency: int) -> Stats:
    stats = Stats()
    measure_from = time.monotonic() + WARMUP_S
    until = measure_from + MEASURE_S
    last_done = until

    async def worker() -> None:
        nonlocal last_done
        while time.monotonic() < until and not stats.failed:
            began = time.monotonic()
            if began >= until:
                return
            stats.started += 1
            try:
                await op(seed)
            except Exception:  # Cancellation is not an operation failure and propagates.
                stats.failed += 1
                return
            done = time.monotonic()
            if began >= measure_from:
                last_done = max(last_done, done)
                stats.ok += 1
                slot = stats.ok - 1 if stats.ok <= 16_384 else random.randrange(stats.ok)
                if slot < 16_384:
                    if slot == len(stats.latencies):
                        stats.latencies.append(done - began)
                    else:
                        stats.latencies[slot] = done - began

    tasks = [asyncio.create_task(worker()) for _ in range(concurrency)]
    try:
        _, pending = await asyncio.wait(tasks, timeout=WARMUP_S + MEASURE_S + DRAIN_S)
        stats.stuck = bool(pending)
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.wait(tasks, timeout=DRAIN_S)
    stats.elapsed_s = last_done - measure_from
    return stats


def discover_regions() -> list[str]:
    try:
        lines = Path(os.environ["TS_PEERS_FILE"]).read_text().splitlines()
        regions = set()
        for line in lines:
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            parts = line.split()
            if len(parts) != 3:
                raise ValueError()
            node, region, address = parts
            ipaddress.IPv4Address(address)
            if not node or not region.isalnum():
                raise ValueError()
            regions.add(region)
        if not set(REGIONS_REQUIRED) <= regions:
            raise ValueError()
        return sorted(regions)
    except (KeyError, OSError, ValueError):
        raise ValueError("serving peers file is missing or malformed") from None


async def run_session(work: Awaitable[None], run_s: float) -> None:
    loop = asyncio.get_running_loop()
    session = asyncio.create_task(work)
    cancelled = False

    def cancel_once() -> None:
        nonlocal cancelled
        if not cancelled:
            cancelled = True
            session.cancel()  # Repeat signals and the run deadline cannot interrupt cleanup.

    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, cancel_once)
    deadline = loop.call_later(run_s, cancel_once)
    try:
        await session
    finally:
        deadline.cancel()
        for sig in (signal.SIGINT, signal.SIGTERM):
            loop.remove_signal_handler(sig)


