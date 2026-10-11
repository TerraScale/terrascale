#!/usr/bin/env bash
# Prove release.sh before it touches real releases: drive it over HTTP against
# a fake Forgejo release API in a scratch repository and check that it drafts
# the tested bytes of both executables with their checksums, refuses a missing
# executable, emits no release_id when an upload fails, replaces only the
# draft an earlier attempt of the same run left, never touches a published
# release or a draft prepared by hand, refuses any ref other than main, and
# publishes a draft.
set -euo pipefail

script=$(cd "$(dirname "$0")" && pwd)/release.sh
work=$(mktemp -d)
server=
cleanup() {
  [[ -z $server ]] || kill "$server" 2>/dev/null || true
  rm -rf -- "$work"
}
trap cleanup EXIT
fail() { echo "release check failed: $*" >&2; exit 1; }
logged() { grep -qs -- "$1" "$work/log"; }

# Check the real HTTP client's deadlines; inject transport cancellation without waiting five minutes.
mkdir -p "$work/bin"
python3 - "$work/bin/curl" "$(command -v curl)" "$work/timeout" <<'PY'
import pathlib, sys
path, client, timeout = sys.argv[1:]
pathlib.Path(path).write_text(f'''#!/usr/bin/env python3
import os, pathlib, sys, urllib.parse
args = sys.argv[1:]
url = urllib.parse.urlparse(args[-1])
name = urllib.parse.parse_qs(url.query).get("name", [""])[0]
binary = name in ["terrabase-linux-amd64", "terrabase-linux-arm64"]
assert args[args.index("--connect-timeout") + 1] == "10"
assert args[args.index("--max-time") + 1] == ("300" if binary else "30")
marker = pathlib.Path({timeout!r})
if marker.exists() and (marker.read_text().strip() == name or
                       marker.read_text().strip() == "lookup" and "/releases/tags/" in url.path):
    sys.exit(28)
os.execv({client!r}, [{client!r}, *args])
''')
PY
chmod +x "$work/bin/curl"
export PATH="$work/bin:$PATH"

# serve PRESET FAILING_ASSET: a fresh fake API on an ephemeral port that keeps
# the releases it creates. PRESET is a release that already exists for any
# other tag: "" (none), "foreign" (a draft prepared by hand) or "published".
# The first upload of FAILING_ASSET answers HTTP 500 and leaves the draft.
serve() {
  [[ -z $server ]] || { kill "$server"; wait "$server" 2>/dev/null || true; }
  rm -f -- "$work/port" "$work/log" "$work/created.json" "$work/patched.json" "$work"/asset-*
  python3 - "$work" "$1" "$2" <<'PY' &
import http.server, json, os, re, sys
work, preset, failing = sys.argv[1:]
releases = {}  # tag -> release
failures = {failing}

class Api(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, code, body=None):
        data = json.dumps(body).encode() if body is not None else b""
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)
        with open(f"{work}/log", "a") as log:
            log.write(f"{self.command} {self.path} {code}\n")

    def body(self):
        return self.rfile.read(int(self.headers.get("Content-Length", 0)))

    def authorized(self):
        if self.headers.get("Authorization") == "token check-token":
            return True
        self.reply(401, {"message": "token is required"})
        return False

    def do_GET(self):
        if not self.authorized():
            return
        tag = self.path.rsplit("/", 1)[1]
        if tag not in releases and preset:
            releases[tag] = {"id": 99, "tag_name": tag, "draft": preset == "foreign", "body": "Notes written by hand"}
        if tag in releases:
            self.reply(200, releases[tag])
        else:
            self.reply(404, {"message": "Not Found"})

    def do_DELETE(self):
        if self.authorized():
            gone = int(self.path.rsplit("/", 1)[1])
            for tag in [tag for tag, release in releases.items() if release["id"] == gone]:
                del releases[tag]
            self.reply(204)

    def do_POST(self):
        if not self.authorized():
            return
        data = self.body()
        asset = re.search(r"/releases/7/assets\?name=(.+)$", self.path)
        if asset:
            name = asset.group(1)
            if name in failures:
                failures.discard(name)
                return self.reply(500, {"message": "storage is full"})
            boundary = self.headers["Content-Type"].split("boundary=")[1].encode()
            part = data.split(b"--" + boundary)[1]
            payload = part.split(b"\r\n\r\n", 1)[1][:-2]
            with open(f"{work}/asset-{name}", "wb") as out:
                out.write(payload)
            return self.reply(201, {"name": name, "size": len(payload)})
        with open(f"{work}/created.json", "wb") as out:
            out.write(data)
        release = {**json.loads(data), "id": 7}
        releases[release["tag_name"]] = release
        self.reply(201, release)

    def do_PATCH(self):
        if self.authorized():
            data = self.body()
            with open(f"{work}/patched.json", "wb") as out:
                out.write(data)
            self.reply(200, {**json.loads(data), "tag_name": "v0", "html_url": "http://fake/release"})

server = http.server.HTTPServer(("127.0.0.1", 0), Api)
with open(f"{work}/port.tmp", "w") as out:
    out.write(str(server.server_port))
os.rename(f"{work}/port.tmp", f"{work}/port")
server.serve_forever()
PY
  server=$!
  for _ in $(seq 100); do [[ -s $work/port ]] && break; sleep 0.05; done
  [[ -s $work/port ]] || fail 'the fake API did not start'
  GITHUB_SERVER_URL="http://127.0.0.1:$(cat "$work/port")"
  export GITHUB_SERVER_URL
}

# draft REF: release.sh draft in the scratch repository; release_id lands in $work/out.
draft() {
  : > "$work/out"
  (cd "$work/repo" && GITHUB_REF=$1 GITHUB_SHA=$(git rev-parse HEAD) bash "$script" draft dist)
}

export FORGEJO_TOKEN=check-token GITHUB_REPOSITORY=TerraScale/terrascale GITHUB_RUN_ID=13105 \
  GITHUB_RUN_NUMBER=4 GITHUB_OUTPUT="$work/out" GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 \
  GIT_AUTHOR_NAME=check GIT_AUTHOR_EMAIL=check@localhost GIT_COMMITTER_NAME=check GIT_COMMITTER_EMAIL=check@localhost

# main with two commits; the project file carries the version the tag check used to read.
repo=$work/repo
git init -q -b main "$repo"
mkdir -p "$repo/src/TerraScale.TerraBase" "$repo/dist"
echo '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>' \
  > "$repo/src/TerraScale.TerraBase/TerraScale.TerraBase.csproj"
echo dist/ > "$repo/.gitignore"
commit() { git -C "$repo" add -A && git -C "$repo" commit -q --allow-empty -m "$1"; }
commit 'Add A'
commit 'Add C'
executables=(terrabase-linux-amd64 terrabase-linux-arm64)
head -c 65536 /dev/urandom > "$repo/dist/terrabase-linux-amd64"

serve '' ''
! draft refs/heads/main 2>/dev/null || fail 'a draft without terrabase-linux-arm64 passed'
! logged '^POST' || fail 'a draft without terrabase-linux-arm64 reached the API'
head -c 70000 /dev/urandom > "$repo/dist/terrabase-linux-arm64"

draft refs/heads/main >/dev/null || fail 'a main push was not drafted'
grep -qx 'release_id=7' "$work/out" || fail 'no release_id for a drafted main push'
jq -e '.draft and .prerelease and (.tag_name | test("^ci-[0-9a-f]{40}-run-13105$"))' "$work/created.json" >/dev/null ||
  fail 'a main push must draft the prerelease ci-<sha>-run-<run id>'
jq -r .body "$work/created.json" > "$work/notes"
for name in "${executables[@]}"; do
  cmp -s "$repo/dist/$name" "$work/asset-$name" || fail "uploaded bytes of $name differ"
  (cd "$work" && mkdir -p verify && cp "asset-$name.sha256" verify/checksum && cp "asset-$name" "verify/$name" &&
    cd verify && sha256sum -c --quiet checksum) || fail "the uploaded checksum does not verify the uploaded $name"
  grep -q "^SHA-256 $name: \`$(cut -d' ' -f1 "$work/asset-$name.sha256")\`" "$work/notes" || fail "the notes miss the SHA-256 of $name"
done
bash "$script" publish 7 >/dev/null || fail 'publishing failed'
logged 'PATCH /api/v1/repos/TerraScale/terrascale/releases/7 200' || fail 'publish did not publish the draft'
jq -e '.draft == false' "$work/patched.json" >/dev/null || fail 'publish did not set draft to false'

# A failed upload leaves a draft and no release_id; rerunning the run replaces that draft.
for asset in "${executables[@]}" "${executables[@]/%/.sha256}"; do
  serve '' "$asset"
  ! draft refs/heads/main 2>/dev/null || fail "a failed upload of $asset passed"
  [[ ! -s $work/out ]] || fail "release_id emitted after a failed upload of $asset"
  draft refs/heads/main >/dev/null || fail "the rerun did not replace the draft left by a failed upload of $asset"
  logged 'DELETE /api/v1/repos/TerraScale/terrascale/releases/7 204' || fail 'the leftover draft was not deleted'
  grep -qx 'release_id=7' "$work/out" || fail 'no release_id after replacing a leftover draft'
done

# Connection/transfer timeouts leave no qualified output; a subsequent attempt can replace the partial draft.
for asset in lookup "${executables[@]}" "${executables[@]/%/.sha256}"; do
  serve '' ''
  printf '%s\n' "$asset" > "$work/timeout"
  ! draft refs/heads/main 2>/dev/null || fail "a timed-out $asset request passed"
  [[ ! -s $work/out ]] || fail "release_id emitted after $asset timed out"
  rm -- "$work/timeout"
  draft refs/heads/main >/dev/null || fail "recovery after a timed-out $asset request failed"
  grep -qx 'release_id=7' "$work/out" || fail "no release_id after recovery from $asset timeout"
done

for preset in published foreign; do
  serve "$preset" ''
  ! draft refs/heads/main 2>/dev/null || fail "a $preset release was replaced"
  ! logged '^DELETE\|^POST' || fail "a $preset release was modified"
  [[ ! -s $work/out ]] || fail "release_id emitted over a $preset release"
done

serve '' ''
! draft refs/tags/v1.2.3 2>/dev/null || fail 'a tag was drafted; only main is released'
! logged '^POST' || fail 'a tag reached the API'
[[ ! -s $work/out ]] || fail 'release_id emitted for a tag'
echo 'release checks passed'
