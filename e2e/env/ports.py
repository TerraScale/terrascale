#!/usr/bin/env python3
import fcntl
import json
import os
import secrets
import socket
import sys
from pathlib import Path

JOURNEY = {
    "caddy": 443,
    "smtp": 587,
    "nats": 4222,
    "nats_http": 8222,
    "postgres_edge": 5432,
    "mailpit": 8025,
    "cloudflare": 8026,
    "stripe": 8027,
    "r2": 8028,
    "hooks": 8029,
    "attack": 8030,
    "oauth": 8031,
    "dns": 8032,
    "sso": 8033,
    "terrabase": 8080,
    "internal": 8081,
    "dashboard": 8085,
    "client_proxy": 8089,
    "auth": 8090,
    "terrabase_fra": 8180,
    "terrabase_gru2": 8280,
    "terrabase_fra2": 8380,
    "metrics": 9091,
    "auth_metrics": 9092,
    "metrics_fra": 9191,
    "metrics_gru2": 9291,
    "metrics_fra2": 9391,
    "postgres": 6432,
    "postgres_fra": 6532,
    "postgres_gru2": 6632,
    "postgres_fra2": 6732,
}


def registry():
    base = os.environ.get("XDG_RUNTIME_DIR")
    root = Path(base) if base else Path("/tmp")
    path = root / "terrascale-e2e-ports" / str(os.stat("/proc/self/ns/net").st_ino)
    path.mkdir(parents=True, mode=0o700, exist_ok=True)
    os.chmod(path, 0o700)
    return path


class Hold:
    def __init__(self, preferred, block=False, exact=False):
        self.root = registry()
        self.block = block
        self.exact = exact
        self._global = (self.root / "lock").open("a+")
        fcntl.flock(self._global, fcntl.LOCK_EX)
        try:
            self._reap()
            self.ports = self._assign(preferred)
            self._claim = self._write()
        finally:
            fcntl.flock(self._global, fcntl.LOCK_UN)

    def _reap(self):
        for lock_path in list(self.root.glob("*.lock")):
            handle = lock_path.open("a+")
            try:
                fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                handle.close()
                continue
            (self.root / (lock_path.name[:-5] + ".json")).unlink(missing_ok=True)
            fcntl.flock(handle, fcntl.LOCK_UN)
            handle.close()
            lock_path.unlink(missing_ok=True)
        for leftover in self.root.glob("*.json"):
            if not (self.root / (leftover.stem + ".lock")).exists():
                leftover.unlink(missing_ok=True)

    def _taken(self):
        taken = set()
        for path in self.root.glob("*.json"):
            taken.update(int(port) for port in json.loads(path.read_text()).values())
        return taken

    def _free(self, port, taken):
        if port in taken or not 1 <= port <= 65535:
            return False
        try:
            with socket.socket() as sock:
                sock.bind(("0.0.0.0", port))
        except OSError:
            return False
        return True

    def _any(self, taken):
        for port in range(20000, 65000):
            if self._free(port, taken):
                return port
        raise SystemExit("e2e port registry: no free port in this network namespace")

    def _assign(self, preferred):
        taken = self._taken()
        named = {name: port for name, port in preferred.items() if port}
        if self.exact or self.block:
            if len(set(named.values())) != len(named):
                raise SystemExit("e2e port registry: duplicate preferred ports")
            if all(self._free(port, taken) for port in named.values()):
                return dict(named)
            if self.exact:
                busy = [str(port) for port in named.values() if not self._free(port, taken)]
                raise SystemExit("e2e port registry: ports in use: " + " ".join(busy))
            for stride in range(1000, 64000, 1000):
                shifted = {name: port + stride for name, port in named.items()}
                if max(shifted.values()) > 65535:
                    break
                if len(set(shifted.values())) != len(shifted):
                    continue
                if all(self._free(port, taken) for port in shifted.values()):
                    return shifted
            raise SystemExit("e2e port registry: no free block in this network namespace")
        assigned = {}
        used = set(taken)
        for name, port in preferred.items():
            chosen = port if port and self._free(port, used) else self._any(used)
            assigned[name] = chosen
            used.add(chosen)
        return assigned

    def _write(self):
        token = secrets.token_hex(8)
        lock = (self.root / (token + ".lock")).open("a+")
        fcntl.flock(lock, fcntl.LOCK_EX)
        path = self.root / (token + ".json")
        path.write_text(json.dumps(self.ports))
        os.chmod(path, 0o600)
        lock.write(str(os.getpid()))
        lock.flush()
        return lock


def parse(argv):
    if not argv or argv[0] != "hold":
        raise SystemExit("usage: ports.py hold --out FILE [--block|--exact] [--profile journey] name=port ...")
    out = None
    block = False
    exact = False
    preferred = {}
    args = argv[1:]
    while args:
        item = args.pop(0)
        if item == "--out":
            out = Path(args.pop(0))
        elif item == "--block":
            block = True
        elif item == "--exact":
            exact = True
        elif item == "--profile":
            name = args.pop(0)
            if name != "journey":
                raise SystemExit("unknown e2e port profile " + name)
            preferred.update(JOURNEY)
        elif "=" in item:
            key, value = item.split("=", 1)
            preferred[key] = int(value)
        else:
            raise SystemExit("unknown ports.py argument " + item)
    if out is None or not preferred:
        raise SystemExit("ports.py hold needs --out and at least one port")
    return out, preferred, block, exact


def main():
    out, preferred, block, exact = parse(sys.argv[1:])
    held = Hold(preferred, block=block, exact=exact)
    temporary = out.with_name(out.name + ".tmp")
    temporary.write_text(json.dumps(held.ports))
    os.chmod(temporary, 0o600)
    temporary.replace(out)
    print("ready", flush=True)
    import signal
    signal.pause()


if __name__ == "__main__":
    main()
