#!/usr/bin/env bash
# Exercise release-app.sh against a fake ts-orch: a release that finishes, and one stopped at its limit with diagnostics.
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
    printf '#!/usr/bin/env bash\necho progress; sleep "$FAKE_SLEEP"; echo "{\\"state\\":\\"done\\"}"\n' > "$directory/terrascale-orchestrator-linux-amd64" ;;
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

FAKE_SLEEP=0 bash "$script" terrabase "$image" 20 > "$work/out"
grep -q '"state":"done"' "$work/out"
grep -q 'Release terrabase (.*, exit 0)' "$GITHUB_STEP_SUMMARY"

set +e
FAKE_SLEEP=30 bash "$script" terrabase "$image" 2 > "$work/out" 2>&1
status=$?
set -e
test "$status" -eq 124
grep -q 'progress' "$work/out"
grep -q 'did not finish within 2s' "$GITHUB_STEP_SUMMARY"
grep -q 'gru: NATS 127.0.0.1:1 unreachable' "$GITHUB_STEP_SUMMARY"
grep -q '::error::Release of terrabase failed' "$work/out"
echo 'release-app-test: ok (finished release, bounded stop with progress and diagnostics)'
