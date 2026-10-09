#!/usr/bin/env python3
"""Cross-region replication check (TS-643, docs/cross-region.md, docs/rollout/ts-643-cross-region.md).

Writes to a dedicated canary database through each region's public API and reads the write back through the other
region, in both directions, then writes one document in every region at once and waits until all serve the same
winner with the same version. Exits 0 when every check passed within the lag threshold, 1 otherwise, 2 on bad usage.

usage: cross-region-check.py [--canary FILE] [--lag-threshold SECONDS]

Only the canary database is written, always the same documents (caller-supplied canary keys) of collection "canary", so it does not
grow. Use a database of its own: TS-660's in-region check owns its canary database.

FILE (mode 0600, default ~/.config/terrabase/cross-region-canary.json) is JSON:
{"project": "<project id>", "database": "<database name>",
 "regions": [{"region": "gru-1", "api": "https://gru.api.terrascale.tech", "secret": "tb_..."},
             {"region": "sfo-1", "api": "https://sfo.api.terrascale.tech", "secret": "tb_..."}]}
Each secret is an API key of that region with documents:read and documents:write on the canary database.
"""

import argparse
import concurrent.futures
import json
import os
import stat
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

COLLECTION = "canary"
# Fixed caller-supplied keys: the canary keeps three documents, overwritten every run.


class Region:
    def __init__(self, name, url, key, project, database):
        self.name = name
        self.base = f"{url.rstrip('/')}/v1/projects/{project}/databases/{database}"
        self.key = key
        self.context = ssl.create_default_context()

    def request(self, method, path, body=None):
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(self.base + path, data=data, method=method)
        request.add_header("Authorization", f"Bearer {self.key}")
        # A request that reaches another region answers 421 instead of being served there.
        request.add_header("X-TerraScale-Region-Pin", self.name)
        # Cloudflare's browser integrity check refuses urllib's default User-Agent with 403 (error 1010).
        request.add_header("User-Agent", "terrabase-cross-region-check/1")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=15, context=self.context) as response:
                return response.status, response.headers.get("ETag"), json.loads(response.read() or b"null")
        except urllib.error.HTTPError as error:
            return error.code, error.headers.get("ETag"), None

    def put(self, name, value):
        status, etag, _ = self.request("PUT", f"/collections/{COLLECTION}/documents/by-key?{urllib.parse.urlencode({'id': name})}", value)
        if status not in (200, 201):
            raise RuntimeError(f"{self.name}: writing {name} answered {status}")
        return etag

    def get(self, name):
        status, etag, body = self.request("GET", f"/collections/{COLLECTION}/documents/by-key?{urllib.parse.urlencode({'id': name})}")
        if status == 404:
            return None, None
        if status != 200:
            raise RuntimeError(f"{self.name}: reading {name} answered {status}")
        return body["document"], etag


def wait(threshold, read, done):
    """Polls read() until done(result) or the threshold passes; returns (result, seconds) or (last, None)."""
    started = time.monotonic()
    last = None
    while time.monotonic() - started <= threshold:
        last = read()
        if done(last):
            return last, time.monotonic() - started
        time.sleep(0.5)
    return last, None


def direction(source, target, nonce, threshold):
    name = f"{source.name}-to-{target.name}"
    etag = source.put(name, {"nonce": nonce, "written_in": source.name, "written_at": time.time()})
    (document, target_etag), lag = wait(threshold, lambda: target.get(name), lambda read: read[0] is not None and read[0].get("nonce") == nonce)
    if lag is None:
        return {"check": name, "ok": False, "error": f"not readable in {target.name} within {threshold} s"}
    if target_etag != etag:
        return {"check": name, "ok": False, "lag_seconds": round(lag, 3), "error": f"version differs: {etag} in {source.name}, {target_etag} in {target.name}"}
    return {"check": name, "ok": True, "lag_seconds": round(lag, 3)}


def convergence(regions, nonce, threshold):
    name = "written-in-every-region"
    with concurrent.futures.ThreadPoolExecutor(len(regions)) as pool:
        list(pool.map(lambda region: region.put(name, {"nonce": f"{nonce}-{region.name}", "written_in": region.name}), regions))

    def read():
        return [region.get(name) for region in regions]

    def converged(reads):
        return all(document is not None and document.get("nonce", "").startswith(nonce) for document, _ in reads) and len({json.dumps(document, sort_keys=True) + str(etag) for document, etag in reads}) == 1

    reads, lag = wait(threshold, read, converged)
    if lag is None:
        return {"check": name, "ok": False, "error": f"regions did not converge within {threshold} s: {[etag for _, etag in reads]}"}
    return {"check": name, "ok": True, "lag_seconds": round(lag, 3), "winner": reads[0][0].get("written_in")}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--canary", default=os.path.expanduser("~/.config/terrabase/cross-region-canary.json"), metavar="FILE")
    parser.add_argument("--lag-threshold", type=float, default=60, metavar="SECONDS", help="longest a write may take to show up in another region (default 60)")
    args = parser.parse_args()

    try:
        if os.stat(args.canary).st_mode & (stat.S_IRWXG | stat.S_IRWXO):
            print(f"cross-region-check: {args.canary} holds API keys; make it mode 0600", file=sys.stderr)
            return 2
        with open(args.canary, encoding="utf-8") as file:
            canary = json.load(file)
        regions = [Region(entry["region"], entry["api"], entry["secret"], canary["project"], canary["database"]) for entry in canary["regions"]]
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(f"cross-region-check: cannot read the canary file: {error!r}", file=sys.stderr)
        return 2
    if len(regions) < 2:
        print("cross-region-check: the canary file needs at least two regions", file=sys.stderr)
        return 2

    nonce = str(uuid.uuid4())
    results = []
    for source in regions:
        # The collection exists after the first run; 409 means it does.
        try:
            status, _, _ = source.request("POST", "/collections", {"name": COLLECTION})
            if status not in (200, 201, 409):
                results.append({"check": f"{source.name}-collection", "ok": False, "error": f"creating the collection answered {status}"})
        except OSError as error:
            results.append({"check": f"{source.name}-collection", "ok": False, "error": repr(error)})
    for source in regions:
        for target in regions:
            if source is not target:
                try:
                    results.append(direction(source, target, nonce, args.lag_threshold))
                except (RuntimeError, OSError, ValueError, KeyError) as error:
                    results.append({"check": f"{source.name}-to-{target.name}", "ok": False, "error": repr(error)})
    try:
        results.append(convergence(regions, nonce, args.lag_threshold))
    except (RuntimeError, OSError, ValueError, KeyError) as error:
        results.append({"check": "written-in-every-region", "ok": False, "error": repr(error)})

    ok = all(result["ok"] for result in results)
    print(json.dumps({"ok": ok, "at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "results": results}))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
