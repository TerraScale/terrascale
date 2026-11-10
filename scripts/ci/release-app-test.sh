#!/usr/bin/env bash
set -euo pipefail
script=$(realpath "$(dirname "${BASH_SOURCE[0]}")/release-app.sh")
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir "$work/bin"
cat > "$work/bin/gh" << 'EOF'
#!/usr/bin/env bash
case "$1 $2" in
  'release list') echo ci-fixture ;;
  'release download')
    directory=$(sed -E 's/.*--dir ([^ ]+).*/\1/' <<< "$*")
    cat > "$directory/terrascale-orchestrator-linux-amd64" << 'FAKE'
#!/usr/bin/env bash
echo 'sjc applied in 40.5s'
if [[ -n ${FAKE_FAIL:-} ]]; then
  echo 'gru failed after 2m0s: not ready within 1m0s: task application readiness returned HTTP 503'
  echo 'rolled back to fixture in sjc, fra, gru' >&2
  exit 1
fi
echo '{"state":"done"}'
FAKE
    ;;
esac
EOF
cat > "$work/bin/curl" << 'EOF'
#!/usr/bin/env bash
for argument in "$@"; do [[ ${previous:-} == -o ]] && output=$argument; previous=$argument; done
if [[ $* == *regions.json* ]]; then echo '[{"region":"gru","nats":"tls://127.0.0.1:1"}]' > "$output"; else printf '200 revision=fixture'; fi
EOF
chmod +x "$work/bin/gh" "$work/bin/curl"
export PATH="$work/bin:$PATH" RUNNER_TEMP=$work GITHUB_STEP_SUMMARY=$work/summary
image=ghcr.io/terrascale/terrabase@sha256:$(printf '%064d' 0)

bash "$script" terrabase "$image" > "$work/out"
grep -q 'sjc applied in 40.5s' "$work/out"
grep -q '"state":"done"' "$work/out"
grep -q 'Release terrabase (.*, exit 0)' "$GITHUB_STEP_SUMMARY"

set +e
FAKE_FAIL=1 bash "$script" terrabase "$image" > "$work/out" 2>&1
status=$?
set -e
test "$status" -eq 1
grep -q 'gru failed after 2m0s: not ready within 1m0s' "$work/out"
grep -q 'Release terrabase (.*, exit 1)' "$GITHUB_STEP_SUMMARY"
grep -q 'gru failed after 2m0s: not ready within 1m0s: task application readiness returned HTTP 503' "$GITHUB_STEP_SUMMARY"
grep -q 'rolled back to fixture in sjc, fra, gru' "$GITHUB_STEP_SUMMARY"
grep -q 'gru: NATS 127.0.0.1:1 unreachable' "$GITHUB_STEP_SUMMARY"
grep -q '::error::Release of terrabase failed' "$work/out"
echo 'release-app-test: ok (finished release, region failure event and diagnostics in the job summary)'
