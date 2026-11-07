#!/usr/bin/env python3
"""Happy path and the stopped-pipeline failure for scripts/dev/ts-ship."""

from __future__ import annotations

import os
import signal
import subprocess
import sys
import tempfile
import textwrap
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WRAPPER = ROOT / "scripts" / "dev" / "ts-ship"


def proc_state(pid: int) -> str:
    data = Path(f"/proc/{pid}/stat").read_text()
    return data[data.rfind(")") + 2]


def ppid_of(pid: int) -> int:
    for line in Path(f"/proc/{pid}/status").read_text().splitlines():
        if line.startswith("PPid:"):
            return int(line.split()[1])
    raise RuntimeError(f"no PPid for {pid}")


def alive(pid: int) -> bool:
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return Path(f"/proc/{pid}").exists()


def write_impl(directory: Path, name: str, body: str) -> Path:
    path = directory / name
    path.write_text(textwrap.dedent(body))
    return path


def start(impl: Path, state: Path, *args: str) -> subprocess.Popen[bytes]:
    env = os.environ.copy()
    env["TS_SHIP_IMPL"] = str(impl)
    env["TS_SHIP_STATE_DIR"] = str(state)
    return subprocess.Popen(
        [sys.executable, str(WRAPPER), *args],
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        start_new_session=True,
    )


def stop(proc: subprocess.Popen[bytes], *pids: int) -> None:
    for pid in pids:
        if pid <= 0:
            continue
        try:
            os.killpg(pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass
        try:
            os.kill(pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass
    if proc.poll() is None:
        try:
            os.killpg(proc.pid, signal.SIGCONT)
        except ProcessLookupError:
            pass
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    try:
        proc.wait(timeout=2)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=2)


def wait_until(predicate, deadline: float, message: str):
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(0.01)
    raise AssertionError(message)


def happy(directory: Path) -> None:
    state = directory / "happy"
    state.mkdir()
    record = state / "record"
    impl = write_impl(
        directory,
        "happy.py",
        """
        import os, sys
        with open(sys.argv[1], "w") as handle:
            handle.write(f"{os.getpid()} {os.getsid(0)} {os.getppid()} {os.getpgrp()}\\n")
        print("SHIPPED test", flush=True)
        raise SystemExit(7)
        """,
    )
    proc = start(impl, state, str(record))
    try:
        stdout, stderr = proc.communicate(timeout=5)
    except subprocess.TimeoutExpired:
        stop(proc)
        raise
    assert proc.returncode == 7, (proc.returncode, stdout, stderr)
    text = stdout.decode()
    assert "detached from job control" in text, text
    assert "pipeline's exit status" in text, text
    assert b"SHIPPED test" not in stdout
    log = next(state.glob("detached-*.log")).read_text()
    assert "SHIPPED test" in log, log
    pid, sid, ppid, pgrp = (int(part) for part in record.read_text().split())
    assert sid == ppid, (pid, sid, ppid, pgrp)
    assert sid != proc.pid
    assert pgrp == pid
    assert pgrp != sid

    sleeper_pid = state / "sleeper"
    sleeper = write_impl(
        directory,
        "sleeper.py",
        """
        import os, sys, time
        with open(sys.argv[1], "w") as handle:
            handle.write(str(os.getpid()))
        time.sleep(30)
        """,
    )
    held = start(sleeper, state / "held", str(sleeper_pid))
    try:
        wait_until(lambda: sleeper_pid.exists(), time.monotonic() + 3, "sleeper did not start")
        child = int(sleeper_pid.read_text())
        os.kill(held.pid, signal.SIGTERM)
        assert held.wait(timeout=3) < 0 or held.returncode not in (0, None)
        wait_until(lambda: not alive(child), time.monotonic() + 3, "ship kept running after the waiter died")
    finally:
        child = int(sleeper_pid.read_text()) if sleeper_pid.exists() else 0
        stop(held, child)


def critical(directory: Path) -> None:
    state = directory / "critical"
    state.mkdir()
    pidfile = state / "pids"
    marker = state / "orphan"
    impl = write_impl(
        directory,
        "critical.py",
        f"""
        import os, sys, time
        from pathlib import Path

        def proc_state(pid):
            data = Path(f"/proc/{{pid}}/stat").read_text()
            return data[data.rfind(")") + 2]

        def ppid_of(pid):
            for line in Path(f"/proc/{{pid}}/status").read_text().splitlines():
                if line.startswith("PPid:"):
                    return int(line.split()[1])
            raise SystemExit(4)

        reaper = os.getppid()
        waiter = ppid_of(reaper)
        Path(sys.argv[1]).write_text(f"{{os.getpid()}} {{reaper}}\\n")
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline and proc_state(waiter) != "T":
            time.sleep(0.01)
        if proc_state(waiter) != "T" or proc_state(os.getpid()) == "T":
            raise SystemExit(4)
        read_fd, write_fd = os.pipe()
        intermediate = os.fork()
        if intermediate == 0:
            if os.fork() == 0:
                os.close(write_fd)
                os.read(read_fd, 1)
                expect = int(os.environ["TS_SHIP_REAPER"])
                ppid = os.getppid()
                limit = time.monotonic() + 2
                while time.monotonic() < limit and ppid != expect:
                    time.sleep(0.01)
                    ppid = os.getppid()
                Path(sys.argv[2]).write_text(f"{{os.getpid()}} {{ppid}}\\n")
                os._exit(0)
            os.close(read_fd)
            os.close(write_fd)
            os._exit(0)
        os.close(read_fd)
        os.close(write_fd)
        os.waitpid(intermediate, 0)
        limit = time.monotonic() + 2
        while time.monotonic() < limit and not Path(sys.argv[2]).exists():
            time.sleep(0.01)
        if not Path(sys.argv[2]).exists():
            raise SystemExit(3)
        orphan, seen = (int(part) for part in Path(sys.argv[2]).read_text().split())
        if seen != reaper:
            raise SystemExit(3)
        limit = time.monotonic() + 2
        while time.monotonic() < limit and Path(f"/proc/{{orphan}}").exists():
            time.sleep(0.01)
        if Path(f"/proc/{{orphan}}").exists():
            raise SystemExit(2)
        raise SystemExit(0)
        """,
    )
    proc = start(impl, state, str(pidfile), str(marker))
    impl_pid = 0
    reaper = 0
    try:
        wait_until(lambda: pidfile.exists(), time.monotonic() + 3, "ship did not start")
        impl_pid, reaper = (int(part) for part in pidfile.read_text().split())
        assert proc_state(reaper) != "T"
        os.killpg(proc.pid, signal.SIGSTOP)
        wait_until(lambda: proc_state(proc.pid) == "T", time.monotonic() + 2, "waiter was not stopped")
        assert proc_state(impl_pid) != "T", proc_state(impl_pid)
        assert proc_state(reaper) != "T", proc_state(reaper)
        wait_until(lambda: marker.exists(), time.monotonic() + 4, "orphan was not adopted")
        adopted = marker.read_text().split()
        assert int(adopted[1]) == reaper, adopted
        orphan = int(adopted[0])
        wait_until(
            lambda: not Path(f"/proc/{orphan}").exists(),
            time.monotonic() + 3,
            "subreaper left the orphan",
        )
        assert proc_state(proc.pid) == "T"
        os.killpg(proc.pid, signal.SIGCONT)
        code = proc.wait(timeout=5)
        detail = b""
        if code != 0:
            logs = list(state.glob("detached-*.log"))
            detail = logs[0].read_bytes() if logs else b""
        assert code == 0, (code, detail)
    finally:
        stop(proc, impl_pid, reaper)


def main() -> None:
    with tempfile.TemporaryDirectory(prefix="ts-ship-supervisor-") as raw:
        directory = Path(raw)
        happy(directory)
        critical(directory)
    print("ship_supervisor_test: ok")


if __name__ == "__main__":
    main()
