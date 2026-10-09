#!/usr/bin/env bash
# Hand the release this main run published to ts-orchestrator, which rolls it
# out to GRU and SFO, health-gated with rollback when a replica never serves
# (ts-orchestrator docs/continuous-delivery.md, "Automatic service releases").
# CI runs this on every green push to main after the release is published.
#
# usage: deploy.sh ROLE ASSET TAG
#   ROLE    the orchestrator role, e.g. ts-auth
#   ASSET   the linux-arm64 release asset, with ASSET.sha256 beside it
#   TAG     the release of this run that carries them
#
# Environment: GITHUB_SERVER_URL, GITHUB_REPOSITORY and GITHUB_SHA of the job;
# ORG_READ_TOKEN, which reads private release assets of this repository and of
# ts-orchestrator; TS_RELEASE_OCI_KEY, the scoped OCI API key that may only
# read the release key in Vault and write releases/v1/services/* in the head
# buckets; and the variables TS_ORCH_RELEASE and TS_ORCH_SHA256 (the CI release whose
# terrascale-orchestrator-linux-amd64 runs the deployment, and that
# executable's SHA-256: bump both together), TS_OBJECT_NAMESPACE, TS_RELEASE_VAULT_ID,
# TS_RELEASE_KEY_SECRET, TS_RELEASE_ACTOR, TS_RELEASE_OCI_USER,
# TS_RELEASE_OCI_TENANCY and TS_RELEASE_OCI_FINGERPRINT, and optionally
# TS_RELEASE_OCI_REGION, the API key's home region (default sa-saopaulo-1).
set -euo pipefail

fail() { echo "$*" >&2; exit 1; }
[[ $# == 3 ]] || { echo "usage: $0 ROLE ASSET TAG" >&2; exit 2; }
role=$1 asset=$2 tag=$3
for variable in GITHUB_SERVER_URL GITHUB_REPOSITORY GITHUB_SHA ORG_READ_TOKEN TS_RELEASE_OCI_KEY \
	TS_ORCH_RELEASE TS_ORCH_SHA256 TS_OBJECT_NAMESPACE TS_RELEASE_VAULT_ID TS_RELEASE_KEY_SECRET TS_RELEASE_ACTOR \
	TS_RELEASE_OCI_USER TS_RELEASE_OCI_TENANCY TS_RELEASE_OCI_FINGERPRINT; do
	[[ -n ${!variable:-} ]] || fail "$variable is required"
done
[[ $(git rev-parse HEAD) == "$GITHUB_SHA" ]] || fail "checkout is not the event revision $GITHUB_SHA"
[[ $TS_ORCH_SHA256 =~ ^[0-9a-f]{64}$ ]] || fail "TS_ORCH_SHA256 must be a lowercase SHA-256"
[[ $(uname -m) == x86_64 ]] || fail "TS_ORCH_SHA256 pins the amd64 ts-orch; this runner is $(uname -m)"
started=$SECONDS

umask 077
work=$(mktemp -d)
# Signals stop ts-orch, if it runs, and exit through the EXIT trap, so the
# key and token files are removed.
child=
stop() {
	# Every child, recorded in child or started but not recorded yet, leads
	# its own process group (setsid). Each group, grandchildren included,
	# gets TERM, then KILL if anything of it still runs 10 s later.
	local pids pid groups=() deadline
	pids=$(pgrep -P "$$" || true)
	pids+=" $child"
	for pid in $pids; do groups+=("-$pid"); done
	if ((${#groups[@]} > 0)); then
		# shellcheck disable=SC2086 # pids is a list of numbers
		kill -TERM -- "${groups[@]}" $pids 2> /dev/null || true
		deadline=$((SECONDS + 10))
		while ((SECONDS < deadline)) && [[ -n $(running "${groups[@]#-}") ]]; do sleep 0.1; done
		# shellcheck disable=SC2086
		kill -KILL -- "${groups[@]}" $pids 2> /dev/null || true
		for pid in $pids; do wait "$pid" 2> /dev/null || true; done
	fi
	exit "$1"
}
# running GROUP...: the processes of the groups that still run; finished
# ones not reaped yet are zombies and do not count.
running() { ps -eo pgid=,stat=,pid= | awk -v groups=" $* " 'index(groups, " " $1 " ") && $2 !~ /^Z/'; }
trap 'rm -rf -- "$work"' EXIT
trap 'stop 129' HUP
trap 'stop 130' INT
trap 'stop 143' TERM
# curl reads the token from a private config file, never from its arguments.
printf 'header = "Authorization: token %s"\n' "$ORG_READ_TOKEN" > "$work/auth"
printf '%s\n' "$TS_RELEASE_OCI_KEY" > "$work/oci-key.pem"
unset ORG_READ_TOKEN TS_RELEASE_OCI_KEY
# The read token reaches only this Forgejo: curl drops a config file's
# Authorization header on a redirect to another host, and since 7.83.0 to
# another port of the same host.
version=$(curl --version | sed -n '1s/^curl \([0-9][0-9.]*\).*/\1/p')
[[ $(printf '%s\n' 7.83.0 "$version" | sort -V | head -1) == 7.83.0 ]] ||
	fail "curl $version may send the read token across a redirect; 7.83.0 or later is required"
# HTTPS only, redirects included: these downloads are executed or deployed.
# Each is bounded: a connection gets 10 s, a transfer slower than 1 KiB/s
# for TS_DOWNLOAD_STALL_SECONDS (default 30) fails, an attempt ends after
# TS_DOWNLOAD_MAX_SECONDS (default 600), and no retry starts after 5
# minutes, so one download takes at most 5 minutes plus one attempt.
stall=${TS_DOWNLOAD_STALL_SECONDS:-30} attempt=${TS_DOWNLOAD_MAX_SECONDS:-600}
[[ $stall =~ ^[1-9][0-9]{0,3}$ && $attempt =~ ^[1-9][0-9]{0,3}$ ]] ||
	fail "TS_DOWNLOAD_STALL_SECONDS and TS_DOWNLOAD_MAX_SECONDS must be 1..9999"
# Like ts-orch below, curl runs as the tracked child in its own process
# group, so a signal stops it, and anything it started, at once.
get() {
	setsid curl -sSfL --proto =https --proto-redir =https --connect-timeout 10 --speed-limit 1024 --speed-time "$stall" \
		--max-time "$attempt" --retry 3 --retry-max-time 300 -K "$work/auth" "$@" &
	child=$!
	local status=0
	wait "$child" || status=$?
	child=
	return "$status"
}

# The exact bytes this run published, from this Forgejo only: the token is
# never sent to an origin a release's metadata names.
for name in "$asset" "$asset.sha256"; do
	get -o "$work/$name" "$GITHUB_SERVER_URL/$GITHUB_REPOSITORY/releases/download/$tag/$name" ||
		fail "release $tag has no $name"
done
# The checksum file must be exactly the line sha256sum writes for the asset.
[[ $(cat -- "$work/$asset.sha256") == "$(cd "$work" && sha256sum -- "$asset")" ]] ||
	fail "$asset does not match its $asset.sha256"

# The pinned ts-orchestrator build drives the release.
get -o "$work/ts-orch" "$GITHUB_SERVER_URL/TerraScale/ts-orchestrator/releases/download/$TS_ORCH_RELEASE/terrascale-orchestrator-linux-amd64" ||
	fail "ts-orchestrator release $TS_ORCH_RELEASE has no terrascale-orchestrator-linux-amd64"
[[ $(sha256sum < "$work/ts-orch" | cut -d' ' -f1) == "$TS_ORCH_SHA256" ]] ||
	fail "ts-orch of $TS_ORCH_RELEASE does not match TS_ORCH_SHA256"
chmod 0700 "$work/ts-orch"

printf '[DEFAULT]\nuser=%s\nfingerprint=%s\ntenancy=%s\nregion=%s\nkey_file=%s\n' "$TS_RELEASE_OCI_USER" \
	"$TS_RELEASE_OCI_FINGERPRINT" "$TS_RELEASE_OCI_TENANCY" "${TS_RELEASE_OCI_REGION:-sa-saopaulo-1}" "$work/oci-key.pem" \
	> "$work/oci-config"
# A tracked child: bash runs a trap only between commands, and wait returns
# for one at once.
# The current signed cell configurations admit canary releases.
OCI_CONFIG_FILE=$work/oci-config setsid "$work/ts-orch" release service-deploy --role "$role" --binary "$work/$asset" \
	--repository "$GITHUB_SERVER_URL/$GITHUB_REPOSITORY" --commit "$GITHUB_SHA" --source-checkout . \
	--environment canary --actor "$TS_RELEASE_ACTOR" \
	--signing-key-secret "$TS_RELEASE_KEY_SECRET" --vault-id "$TS_RELEASE_VAULT_ID" \
	--namespace "$TS_OBJECT_NAMESPACE" --region-timeout 30m --poll 5s --cell GRU --cell SFO &
child=$!
status=0
wait "$child" || status=$?
child=
printf '%s deployment finished in %ss (exit %s)\n' "$role" "$((SECONDS - started))" "$status"
exit "$status"
