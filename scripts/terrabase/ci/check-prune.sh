#!/usr/bin/env bash
# Exercise always-on pruning against an isolated HTTP release API.
set -euo pipefail
script=$(cd "$(dirname "$0")" && pwd)/prune-releases.sh
work=$(mktemp -d)
server=
cleanup() {
  [[ -z $server ]] || { kill "$server" 2>/dev/null || true; wait "$server" 2>/dev/null || true; }
  rm -rf -- "$work"
}
trap cleanup EXIT
python3 - "$work" <<'PY' &
import datetime, http.server, json, pathlib, sys, urllib.parse
work = pathlib.Path(sys.argv[1])
now = datetime.datetime.now(datetime.timezone.utc)
digest = "a" * 64
releases = [{"id": i, "tag_name": f"ci-{i:040x}-run-{i}", "prerelease": True,
             "created_at": (now - datetime.timedelta(days=40-i)).strftime("%Y-%m-%dT%H:%M:%SZ"),
             "assets": []} for i in range(1, 26)]
# Regular releases push the newest 20 CI releases onto pages 10 and 11.
releases[5:5] = [{**releases[0], "id": i, "tag_name": f"v1.0.{i-26}", "prerelease": False}
                 for i in range(26, 502)]

class Api(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def reply(self, code, body):
        data = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)
    def do_GET(self):
        if self.headers.get("Authorization") != "token check-token":
            return self.reply(401, {})
        request = urllib.parse.urlsplit(self.path)
        if request.path == "/api/v1/repos/TerraScale/terrabase/releases":
            query = urllib.parse.parse_qs(request.query)
            page, limit = int(query["page"][0]), int(query["limit"][0])
            start = (page - 1) * limit
            rows = json.loads((work / "releases.json").read_text())
            return self.reply(200, rows[start:start + limit])
        if self.path == "/3.json": return self.reply(200, {"sha256": digest})
        return self.reply(403, {}) # Unreadable manifest must preserve its release.
    def do_DELETE(self):
        if self.headers.get("Authorization") != "token check-token":
            return self.reply(401, {})
        with (work / "deleted").open("a") as log:
            log.write(self.path.rsplit("/", 1)[1] + "\n")
        self.reply(204, None)

server = http.server.HTTPServer(("127.0.0.1", 0), Api)
url = f"http://127.0.0.1:{server.server_port}"
for i in (3, 4):
    releases[i-1]["assets"] = [{"name": "release.json", "size": 64, "browser_download_url": f"{url}/{i}.json"}]
(work / "releases.json").write_text(json.dumps(releases))
(work / "pins").write_text(f"{2:040x}\n{digest}\n")
(work / "url.tmp").write_text(url)
(work / "url.tmp").rename(work / "url")
server.serve_forever()
PY
server=$!
for ((attempt = 0; attempt < 100; attempt++)); do
  [[ ! -f $work/url ]] || break
  sleep 0.05
done
[[ -f $work/url ]] || { echo 'prune test API did not start' >&2; exit 1; }
export FORGEJO_SERVER_URL=$(cat "$work/url") FORGEJO_TOKEN=check-token
export TS_PRUNE_KEEP=20 TS_PRUNE_KEEP_DAYS=7
bash "$script" TerraScale/terrabase "$work/pins" > "$work/result"
printf '5\n1\n' > "$work/expected"
diff -u "$work/expected" "$work/deleted"
grep -q '^TS_RELEASE_PRUNE_V1 repository=TerraScale/terrabase releases=501 deleted=2 kept_pinned=3 ' "$work/result"
: > "$work/no-pins"
if bash "$script" TerraScale/terrabase "$work/no-pins" > "$work/refused" 2>&1; then
  echo 'pruning without live pins unexpectedly succeeded' >&2; exit 1
fi
diff -u "$work/expected" "$work/deleted"
# The 21st recent release must survive by age alone, while the older one is deleted.
python3 - "$work" <<'PY'
import datetime, json, pathlib, sys
work = pathlib.Path(sys.argv[1])
now = datetime.datetime.now(datetime.timezone.utc)
releases = [{"id": i, "tag_name": f"ci-{i:040x}-run-{i}", "prerelease": True,
             "created_at": (now - datetime.timedelta(hours=i-100)).strftime("%Y-%m-%dT%H:%M:%SZ"),
             "assets": []} for i in range(101, 122)]
releases += [{"id": 100, "tag_name": f"ci-{100:040x}-run-100", "prerelease": True,
              "created_at": (now - datetime.timedelta(days=30)).strftime("%Y-%m-%dT%H:%M:%SZ"),
              "assets": []}]
(work / "releases.json").write_text(json.dumps(releases))
PY
: > "$work/deleted"
bash "$script" TerraScale/terrabase "$work/pins" > "$work/result"
printf '100\n' > "$work/expected"
diff -u "$work/expected" "$work/deleted"
grep -q 'deleted=1 kept_pinned=0' "$work/result"
echo 'prune checks passed'
