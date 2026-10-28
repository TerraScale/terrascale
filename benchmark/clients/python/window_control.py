"""Qualify window tails, peers parsing and repeated-signal cleanup without a tenant."""

import asyncio
import json
import os
import signal
import tempfile
from pathlib import Path

import runtime as r


async def main():
    previous = os.environ.get("TS_PEERS_FILE")
    try:
        with tempfile.TemporaryDirectory() as directory:
            peers = Path(directory) / "peers"
            os.environ["TS_PEERS_FILE"] = str(peers)
            peers.write_text("one gru 127.0.0.1\ntwo sjc 127.0.0.2\nthree fra 127.0.0.3\nfour gru 127.0.0.4\n")
            assert r.discover_regions() == ["fra", "gru", "sjc"]
            peers.write_text("one gru\n")
            try:
                r.discover_regions()
            except ValueError:
                pass
            else:
                raise AssertionError("malformed peers accepted")
    finally:
        if previous is None:
            os.environ.pop("TS_PEERS_FILE", None)
        else:
            os.environ["TS_PEERS_FILE"] = previous
    stats = await r.measure(lambda _: asyncio.sleep(2.5), None, 1)
    assert stats.started == 3 and stats.ok == 2 and len(stats.latencies) == 2
    assert stats.elapsed_s > r.MEASURE_S and not stats.failed and not stats.stuck
    cleaned = False

    async def session(_):
        nonlocal cleaned
        try:
            await asyncio.Event().wait()
        finally:
            await asyncio.sleep(.1)
            cleaned = True

    loop = asyncio.get_running_loop()
    first = loop.call_later(.01, os.kill, os.getpid(), signal.SIGINT)
    second = loop.call_later(.02, os.kill, os.getpid(), signal.SIGTERM)
    try:
        try:
            await r.run_session(session(None), 10)
        except asyncio.CancelledError:
            pass
        assert cleaned
    finally:
        first.cancel()
        second.cancel()
    print(json.dumps({"started": stats.started, "ok": stats.ok, "elapsed_s": stats.elapsed_s,
                      "tail_retained": True, "peers_gate": True, "repeat_signal_cleanup": cleaned}))


if __name__ == "__main__":
    asyncio.run(main())
