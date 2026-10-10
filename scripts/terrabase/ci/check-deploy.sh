#!/usr/bin/env bash
# Exercise scripts/ci/deploy.sh against a stub curl and a stub ts-orch: the
# exact ts-orch invocation, with the release bytes, OCI config and key it
# reads; scope discovered by ts-orch without caller flags or a switch; nothing run
# unless the asset matches its exact checksum line
# and ts-orch its pinned SHA-256; a ts-orch failure failing the job; and every
# authenticated download staying on the Forgejo origin.
set -euo pipefail

repo=$(cd "$(dirname "$0")/../.." && pwd)
# The real-curl cases reach local origins directly, whatever proxy the
# runner sets; deploy.sh itself keeps the runner's proxy.
unset HTTPS_PROXY https_proxy HTTP_PROXY http_proxy ALL_PROXY all_proxy
export NO_PROXY='*' no_proxy='*'
sandbox=$(mktemp -d)
# Background deployments run in their own process groups (setsid), which
# the EXIT trap kills with everything they started.
groups=()
cleanup() {
	local group
	for group in ${groups[@]+"${groups[@]}"}; do kill -KILL -- "-$group" 2> /dev/null || true; done
	[[ -z ${origins:-} ]] || kill "$origins" 2> /dev/null || true
	rm -rf -- "$sandbox"
}
trap cleanup EXIT
mkdir -p "$sandbox/repo/scripts/ci" "$sandbox/bin" "$sandbox/serve" "$sandbox/tmp"
cp "$repo/scripts/ci/deploy.sh" "$sandbox/repo/scripts/ci/"
git -C "$sandbox/repo" init -q
git -C "$sandbox/repo" -c user.name=ci -c user.email=ci@example.com commit -q --allow-empty -m fixture

# The stub ts-orch records its arguments and copies the release binary, the
# OCI config and the key while they exist, failing if any is missing; it
# exits with STUB_EXIT.
cat > "$sandbox/serve/ts-orch" <<EOF
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "\$@" > "$sandbox/ran"
previous=
for argument in "\$@"; do
	[[ \$previous == --binary ]] && cp -- "\$argument" "$sandbox/binary-seen"
	previous=\$argument
done
cp -- "\$OCI_CONFIG_FILE" "$sandbox/config-seen"
cp -- "\$(sed -n 's/^key_file=//p' "\$OCI_CONFIG_FILE")" "$sandbox/key-seen"
if [[ \${STUB_BLOCK:-} == stubborn ]]; then
	# One sleep that inherits the ignored TERM, and a grandchild that
	# ignores it too: the group KILL must reach both.
	trap '' TERM
	sleep 1000 &
	printf '%s\n' \$! > "$sandbox/grandchild"
	printf '%s\n' \$\$ > "$sandbox/child"
	exec sleep 1000
elif [[ -n \${STUB_BLOCK:-} ]]; then
	# deploy.sh signals the whole group: TERM ends the current sleep, then
	# runs the trap; no sleep outlives the stub.
	trap 'touch "$sandbox/terminated"; exit 143' TERM
	printf '%s\n' \$\$ > "$sandbox/child"
	while :; do sleep 0.1; done
fi
exit "\${STUB_EXIT:-0}"
EOF
printf 'release bytes\n' > "$sandbox/serve/app-linux-arm64"
# curl logs each URL and call, with the auth config it reads, and serves
# the sandbox file named by the URL's last segment; it fails like curl when
# it cannot read the config or the file.
cat > "$sandbox/bin/curl" <<EOF
#!/usr/bin/env bash
set -euo pipefail
if [[ \$1 == --version ]]; then
	echo "curl \${STUB_CURL_VERSION:-8.0.0} (stub)"
	exit 0
fi
printf '%s\n' "\$*" >> "$sandbox/calls"
out= url=
while (( \$# )); do
	case \$1 in
		-o) out=\$2; shift ;;
		-K) cat -- "\$2" >> "$sandbox/auth-seen"; shift ;;
		--proto|--proto-redir|--retry|--retry-max-time|--connect-timeout|--speed-limit|--speed-time|--max-time) shift ;;
		-sSfL) ;;
		# A new curl option of deploy.sh needs a conscious stub update.
		-*) echo "stub curl: unexpected argument \$1" >&2; exit 64 ;;
		*) url=\$1 ;;
	esac
	shift
done
printf '%s\n' "\$url" >> "$sandbox/urls"
case \$url in
	*/terrascale-orchestrator-linux-amd64) cp "$sandbox/serve/ts-orch" "\$out" ;;
	*) [[ -e $sandbox/serve/\${url##*/} ]] || exit 22; cp "$sandbox/serve/\${url##*/}" "\$out" ;;
esac
EOF
chmod +x "$sandbox/bin/curl"

head=$(git -C "$sandbox/repo" rev-parse HEAD)
export PATH="$sandbox/bin:$PATH" GITHUB_SERVER_URL=https://git.example GITHUB_REPOSITORY=TerraScale/app \
	GITHUB_SHA=$head ORG_READ_TOKEN=token TS_RELEASE_OCI_KEY=oci-private-key \
	TS_ORCH_RELEASE=ci-x TS_OBJECT_NAMESPACE=ns TS_RELEASE_VAULT_ID=vault \
	TS_RELEASE_KEY_SECRET=secret TS_RELEASE_ACTOR=ci-release TS_RELEASE_OCI_USER=user TS_RELEASE_OCI_TENANCY=tenancy \
	TS_RELEASE_OCI_FINGERPRINT=fingerprint
# Optional inputs and stub controls keep their defaults unless a case sets
# them, whatever the caller's environment holds.
unset TS_RELEASE_OCI_REGION TS_DOWNLOAD_STALL_SECONDS TS_DOWNLOAD_MAX_SECONDS STUB_BLOCK STUB_EXIT STUB_CURL_VERSION
pinned=$(sha256sum < "$sandbox/serve/ts-orch" | cut -d' ' -f1)
(cd "$sandbox/serve" && sha256sum app-linux-arm64 > app-linux-arm64.sha256)
cp "$sandbox/serve/app-linux-arm64.sha256" "$sandbox/good.sha256"

fail() { echo "check-deploy: $*" >&2; exit 1; }
python3 - "$repo/.forgejo/workflows/fast.yml" "$repo/scripts/ci/deploy.sh" <<'PY'
import pathlib, re, sys
workflow, script = [pathlib.Path(path).read_text() for path in sys.argv[1:]]
if "TS_AUTO_DEPLOY_" in workflow or "TS_AUTO_DEPLOY_" in script:
    sys.exit("check-deploy: an automatic deployment switch remains")
deploy = workflow.split("\n  deploy:\n", 1)[1]
condition = next(line.strip() for line in deploy.splitlines() if line.strip().startswith("if:"))
if condition != "if: ${{ forgejo.event_name == 'push' && forgejo.ref == 'refs/heads/main' }}":
    sys.exit("check-deploy: deployment must run on every green main push")
if "    needs: [publish]\n" not in deploy:
    sys.exit("check-deploy: deployment must wait for the published green build")
for name, pattern in (("TS_ORCH_RELEASE", r"ci-[0-9a-f]{40}-run-[0-9]+"),
                      ("TS_ORCH_SHA256", r"[0-9a-f]{64}")):
    value = next(line.split(":", 1)[1].strip() for line in deploy.splitlines()
                 if line.strip().startswith(name + ":"))
    if not re.fullmatch(pattern, value):
        sys.exit(f"check-deploy: {name} must be pinned in the workflow")
PY
# alive SESSION: the processes of SESSION that still run. A container whose
# init never reaps orphans keeps finished ones as zombies, which do not.
alive() { ps -eo sid=,stat=,pid=,args= | awk -v sid="$1" '$1 == sid && $2 !~ /^Z/'; }
# live PID: whether PID still runs; a zombie does not.
live() {
	local stat
	stat=$(ps -o stat= -p "$1" 2> /dev/null) && [[ $stat != Z* ]]
}
# emptied: deploy.sh, run with TMPDIR=$sandbox/tmp, left no key or token file.
emptied() { [[ -z $(ls -A "$sandbox/tmp") ]] || fail "deploy.sh left its key and token files: $(ls -A "$sandbox/tmp")"; }
# deploy SHA: run deploy.sh; its status, and whether ts-orch ran.
deploy() {
	rm -f -- "$sandbox/ran" "$sandbox/binary-seen" "$sandbox/config-seen" "$sandbox/key-seen" "$sandbox/urls" \
		"$sandbox/calls" "$sandbox/auth-seen"
	local status=0
	(cd "$sandbox/repo" && TMPDIR=$sandbox/tmp TS_ORCH_SHA256=$1 \
		bash scripts/ci/deploy.sh app app-linux-arm64 v1) > "$sandbox/log" 2>&1 || status=$?
	emptied
	return "$status"
}

deploy "$pinned" || { cat "$sandbox/log" >&2; fail "automatic deployment failed"; }
expected=(release service-deploy --role app --binary '*' --repository https://git.example/TerraScale/app
	--commit "$head" --source-checkout . --actor ci-release --signing-key-secret secret
	--vault-id vault --namespace ns --poll 5s)
mapfile -t ran < "$sandbox/ran"
((${#ran[@]} == ${#expected[@]})) || fail "ts-orch ran with $(paste -sd' ' "$sandbox/ran")"
for index in "${!expected[@]}"; do
	[[ ${expected[index]} == '*' || ${ran[index]} == "${expected[index]}" ]] ||
		fail "ts-orch argument $index is ${ran[index]}, want ${expected[index]}"
done
cmp -s "$sandbox/binary-seen" "$sandbox/serve/app-linux-arm64" || fail "ts-orch got other release bytes"
[[ $(<"$sandbox/key-seen") == oci-private-key ]] || fail "ts-orch got another OCI key"
grep -qx 'user=user' "$sandbox/config-seen" && grep -qx 'fingerprint=fingerprint' "$sandbox/config-seen" &&
	grep -qx 'tenancy=tenancy' "$sandbox/config-seen" && grep -qx 'region=sa-saopaulo-1' "$sandbox/config-seen" || fail "the OCI config is wrong: $(<"$sandbox/config-seen")"
while read -r url; do
	[[ $url == https://git.example/* ]] || fail "a download left the Forgejo origin: $url"
done < "$sandbox/urls"
grep -qx 'https://git.example/TerraScale/app/releases/download/v1/app-linux-arm64' "$sandbox/urls" ||
	fail "the asset was not read from this repository's release"
grep -qx "https://git.example/TerraScale/ts-orchestrator/releases/download/$TS_ORCH_RELEASE/terrascale-orchestrator-linux-amd64" \
	"$sandbox/urls" || fail "ts-orch was not read from its pinned release"
# Every download is HTTPS only, redirects included, sends the token from a
# config file, and never trusts a redirect's origin with it.
while read -r call; do
	[[ " $call " == *" -K "* && " $call " == *" --proto =https "* && " $call " == *" --proto-redir =https "* &&
		" $call " == *" --connect-timeout 10 "* && " $call " == *" --speed-limit 1024 "* &&
		" $call " == *" --speed-time 30 "* && " $call " == *" --max-time 600 "* && " $call " == *" --retry 3 "* &&
		" $call " == *" --retry-max-time 300 "* &&
		" $call " != *" --location-trusted "* && " $call " != *" -k "* && " $call " != *" --insecure "* &&
		" $call " != *" --proxy-insecure "* && $call != *token* ]] || fail "an unsafe download: curl $call"
done < "$sandbox/calls"
(($(grep -c . "$sandbox/calls") == 3)) || fail "expected 3 downloads: $(<"$sandbox/calls")"
[[ $(sort -u "$sandbox/auth-seen") == 'header = "Authorization: token token"' ]] ||
	fail "the downloads did not authenticate with the read token"

if STUB_EXIT=3 deploy "$pinned"; then fail "a failed ts-orch passed"; fi
[[ -e $sandbox/ran ]] || fail "ts-orch did not run before failing"
# TERM to deploy.sh alone, while ts-orch runs: it stops ts-orch, killing
# one that ignores TERM after 10 s, and removes the key and token files.
for block in 1 stubborn; do
rm -f -- "$sandbox/child" "$sandbox/terminated" "$sandbox/grandchild"
mkdir -p "$sandbox/tmp"
(cd "$sandbox/repo" && STUB_BLOCK=$block TMPDIR=$sandbox/tmp TS_ORCH_SHA256=$pinned \
	exec setsid bash scripts/ci/deploy.sh app app-linux-arm64 v1) > "$sandbox/log" 2>&1 &
parent=$!
groups+=("$parent")
for _ in $(seq 100); do [[ -s $sandbox/child ]] && break; sleep 0.1; done
[[ -s $sandbox/child ]] || fail "ts-orch never started: $(<"$sandbox/log")"
# deploy.sh runs ts-orch in its own process group (setsid): track it too.
groups+=("$(<"$sandbox/child")")
kill -TERM "$parent"
for _ in $(seq 150); do kill -0 "$parent" 2> /dev/null || break; sleep 0.1; done
if kill -0 "$parent" 2> /dev/null; then
	kill -KILL -- "-$(<"$sandbox/child")" "$parent" 2> /dev/null || true
	fail "deploy.sh did not stop within 15 s of TERM ($block ts-orch)"
fi
status=0
wait "$parent" || status=$?
((status == 143)) || fail "deploy.sh exited $status after TERM, want 143"
# deploy.sh led its own session: nothing of it, the watchdog's sleep
# included, may outlive it (a stub's last 0.1 s sleep may linger a moment).
for _ in $(seq 10); do [[ -n $(alive "$parent") ]] || break; sleep 0.1; done
[[ -z $(alive "$parent") ]] || fail "processes of the deployment remain: $(alive "$parent")"
[[ $block == stubborn || -e $sandbox/terminated ]] || fail "ts-orch was not stopped"
[[ $block != stubborn ]] || ! live "$(<"$sandbox/grandchild")" ||
	fail "a process ts-orch started outlived deploy.sh"
! live "$(<"$sandbox/child")" || fail "ts-orch still runs after deploy.sh exited ($block)"
emptied
# Checked and empty: forget the group, whose ID may now be reused.
groups=()
done
# A curl that may send the read token across a redirect is refused.
if STUB_CURL_VERSION=7.82.0 deploy "$pinned" || [[ -e $sandbox/ran ]]; then fail "an old curl deployed"; fi
grep -q 'curl 7.82.0 may send the read token' "$sandbox/log" || fail "the old curl was not named: $(<"$sandbox/log")"
if deploy "$(printf '%064d' 0)" || [[ -e $sandbox/ran ]]; then fail "an unpinned ts-orch ran"; fi
grep -q 'does not match TS_ORCH_SHA256' "$sandbox/log" || fail "the ts-orch digest mismatch was not named"
# A checksum line that names the asset with another hash must not pass.
printf '%064d  app-linux-arm64\n' 0 > "$sandbox/serve/app-linux-arm64.sha256"
if deploy "$pinned" || [[ -e $sandbox/ran ]]; then fail "an asset whose checksum hash differs deployed"; fi
grep -q 'does not match its app-linux-arm64.sha256' "$sandbox/log" || fail "the hash mismatch was not named"
# Only the exact line passes: not beside a conflicting line, nor followed by
# other content.
for checksum in "$(cat "$sandbox/good.sha256")"$'\n'"$(printf '%064d' 1)  app-linux-arm64" \
	"$(cat "$sandbox/good.sha256") trailing"; do
	printf '%s\n' "$checksum" > "$sandbox/serve/app-linux-arm64.sha256"
	if deploy "$pinned" || [[ -e $sandbox/ran ]]; then fail "a checksum file with more than the asset's line deployed"; fi
	grep -q 'does not match its app-linux-arm64.sha256' "$sandbox/log" || fail "the extra checksum content was not named"
done
# A checksum of another file must not pass for the asset.
: > "$sandbox/serve/empty"
(cd "$sandbox/serve" && sha256sum empty > app-linux-arm64.sha256)
if deploy "$pinned" || [[ -e $sandbox/ran ]]; then fail "an asset whose checksum names another file deployed"; fi
grep -q 'does not match its app-linux-arm64.sha256' "$sandbox/log" || fail "the name mismatch was not named"

# Real curl against two HTTPS origins: the Forgejo origin redirects the asset
# to another origin, which must never receive the read token.
cp "$sandbox/good.sha256" "$sandbox/serve/app-linux-arm64.sha256"
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 1 -subj /CN=localhost \
	-addext 'subjectAltName=DNS:localhost,IP:127.0.0.1' -keyout "$sandbox/tls.key" -out "$sandbox/tls.crt" 2> /dev/null ||
	fail "openssl cannot generate the test certificate (needs 1.1.1 or later for -addext)"
cat > "$sandbox/origins.py" <<'PY'
import http.server, os, ssl, sys, threading, time
serve, ready, log = sys.argv[1], sys.argv[2], sys.argv[3]
ports = []
def handler(origin):
    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            with open(log, "a") as f:
                f.write(f"{origin} {self.path} {self.headers.get('Authorization', '-')}\n")
            name = self.path.rsplit("/", 1)[-1]
            if "/stall/" in self.path:
                # Headers and a few bytes, then nothing: a stalled transfer.
                self.send_response(200)
                self.send_header("Content-Length", "1000000")
                self.end_headers()
                self.wfile.write(b"x" * 16)
                self.wfile.flush()
                threading.Event().wait()
            if "/trickle/" in self.path:
                # Fast enough never to stall, too slow ever to finish.
                self.send_response(200)
                self.send_header("Content-Length", "100000000")
                self.end_headers()
                try:
                    while True:
                        self.wfile.write(b"x" * 4096)
                        self.wfile.flush()
                        time.sleep(0.5)
                except OSError:
                    return  # curl gave up, as it must
            if origin == "forgejo" and name == "app-linux-arm64":
                self.send_response(302)
                # Tag samehost keeps the host name and changes only the port.
                host = "localhost" if "/samehost/" in self.path else "127.0.0.1"
                self.send_header("Location", f"https://{host}:{ports[1]}/storage/{name}")
                self.end_headers()
                return
            path = os.path.join(serve, "ts-orch" if name == "terrascale-orchestrator-linux-amd64" else name)
            if not os.path.isfile(path):
                self.send_error(404)
                return
            data = open(path, "rb").read()
            self.send_response(200)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        def log_message(self, *args):
            pass
    return Handler
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(os.path.join(os.path.dirname(log), "tls.crt"), os.path.join(os.path.dirname(log), "tls.key"))
for origin in ("forgejo", "storage"):
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler(origin))
    server.socket = context.wrap_socket(server.socket, server_side=True)
    ports.append(server.server_address[1])
    threading.Thread(target=server.serve_forever, daemon=True).start()
with open(ready, "w") as f:
    f.write(f"{ports[0]}\n")
threading.Event().wait()
PY
python3 "$sandbox/origins.py" "$sandbox/serve" "$sandbox/origins.ready" "$sandbox/requests" &
origins=$!
for _ in $(seq 50); do [[ -s $sandbox/origins.ready ]] && break; sleep 0.1; done
[[ -s $sandbox/origins.ready ]] || fail "the HTTPS origins did not start"
# redirected TAG: deploy through the Forgejo origin's redirect of the asset;
# only Forgejo may receive the read token.
redirected() {
	rm -f -- "$sandbox/ran" "$sandbox/binary-seen" "$sandbox/config-seen" "$sandbox/key-seen" "$sandbox/requests"
	(cd "$sandbox/repo" && PATH=${PATH#"$sandbox/bin:"} CURL_CA_BUNDLE=$sandbox/tls.crt TMPDIR=$sandbox/tmp \
		GITHUB_SERVER_URL=https://localhost:$(<"$sandbox/origins.ready") TS_ORCH_SHA256=$pinned \
		bash scripts/ci/deploy.sh app app-linux-arm64 "$1") > "$sandbox/log" 2>&1 ||
		{ cat "$sandbox/log" >&2; fail "the deployment through the $1 redirect failed"; }
	emptied
	[[ -e $sandbox/ran ]] || fail "ts-orch did not run after the $1 redirect"
	cmp -s "$sandbox/binary-seen" "$sandbox/serve/app-linux-arm64" || fail "ts-orch got other bytes through the $1 redirect"
	grep -q "^forgejo /TerraScale/app/releases/download/$1/app-linux-arm64 token token\$" "$sandbox/requests" ||
		fail "the Forgejo origin did not get the read token: $(<"$sandbox/requests")"
	grep -q '^storage /storage/app-linux-arm64 ' "$sandbox/requests" ||
		fail "the $1 redirect target was never reached: $(<"$sandbox/requests")"
	# Every request to the redirect target, retries included, has no token.
	if grep '^storage ' "$sandbox/requests" | grep -qv ' -$'; then
		fail "the $1 redirect target got the read token: $(<"$sandbox/requests")"
	fi
}
# Another host, then the same host on another port.
redirected v1
redirected samehost

# TERM to deploy.sh alone during a stalled download stops curl at once and
# removes the key and token files.
rm -rf -- "$sandbox/tmp" "$sandbox/requests"
mkdir "$sandbox/tmp"
(cd "$sandbox/repo" && PATH=${PATH#"$sandbox/bin:"} CURL_CA_BUNDLE=$sandbox/tls.crt TMPDIR=$sandbox/tmp \
	GITHUB_SERVER_URL=https://localhost:$(<"$sandbox/origins.ready") TS_ORCH_SHA256=$pinned \
	exec setsid bash scripts/ci/deploy.sh app app-linux-arm64 stall) > "$sandbox/log" 2>&1 &
parent=$!
groups+=("$parent")
for _ in $(seq 100); do grep -qs '/stall/' "$sandbox/requests" && break; sleep 0.1; done
grep -qs '/stall/' "$sandbox/requests" || fail "the stalled download never started: $(<"$sandbox/log")"
kill -TERM "$parent"
for _ in $(seq 100); do kill -0 "$parent" 2> /dev/null || break; sleep 0.1; done
if kill -0 "$parent" 2> /dev/null; then
	kill -KILL "$parent" 2> /dev/null || true
	pkill -KILL -f -- "$sandbox/tmp/" || true
	fail "deploy.sh did not stop within 10 s of TERM during a download"
fi
status=0
wait "$parent" || status=$?
((status == 143)) || fail "deploy.sh exited $status after TERM during a download, want 143"
for _ in $(seq 10); do [[ -n $(alive "$parent") ]] || break; sleep 0.1; done
[[ -z $(alive "$parent") ]] || fail "processes of the deployment remain: $(alive "$parent")"
! pgrep -f -- "$sandbox/tmp/" > /dev/null || fail "curl still runs after deploy.sh exited"
emptied
groups=()
# A stalled download fails by the low-speed limit alone (the attempt
# deadline is far), and one that trickles too fast for it fails by the
# attempt deadline alone; nothing runs.
for bounds in "stall 1 100 Operation too slow" "trickle 60 2 Operation timed out after"; do
	read -r tag stall attempt reason <<< "$bounds"
	rm -f -- "$sandbox/ran" "$sandbox/requests"
	started=$SECONDS
	if (cd "$sandbox/repo" && PATH=${PATH#"$sandbox/bin:"} CURL_CA_BUNDLE=$sandbox/tls.crt TS_DOWNLOAD_STALL_SECONDS=$stall \
		TMPDIR=$sandbox/tmp TS_DOWNLOAD_MAX_SECONDS=$attempt GITHUB_SERVER_URL=https://localhost:$(<"$sandbox/origins.ready") \
		TS_ORCH_SHA256=$pinned timeout 60 bash scripts/ci/deploy.sh app app-linux-arm64 "$tag") \
		> "$sandbox/log" 2>&1; then
		fail "a $tag download deployed"
	fi
	[[ ! -e $sandbox/ran ]] || fail "ts-orch ran after a $tag download"
	emptied
	# Four attempts of at most 1 s (stall) or 2 s (trickle), plus curl's 1,
	# 2 and 4 s retry waits: at most 15 s, and 25 s with room for a slow
	# runner, far below the other bound.
	((SECONDS - started <= 25)) || fail "a $tag download took $((SECONDS - started)) s, more than its bound"
	# The intended limit ended it: the origin was reached, curl names the
	# limit, and at least two attempts and a retry wait went by.
	((SECONDS - started >= 3)) || fail "a $tag download failed after $((SECONDS - started)) s, before any limit"
	# One attempt and deploy.sh's 3 retries.
	attempts=$(grep -c "^forgejo /TerraScale/app/releases/download/$tag/app-linux-arm64 " "$sandbox/requests" || true)
	((attempts == 4)) || fail "the $tag download made $attempts attempts, want 4: $(<"$sandbox/requests")"
	grep -q "$reason" "$sandbox/log" || fail "the $tag download did not end by its limit: $(<"$sandbox/log")"
	grep -q "release $tag has no app-linux-arm64" "$sandbox/log" || fail "the $tag download was not reported: $(<"$sandbox/log")"
done
echo "deploy guards passed"
