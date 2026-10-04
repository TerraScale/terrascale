#!/usr/bin/env bash
# Download release asset NAME of TAG to DESTINATION with the read profile
# (FJ_READ_PROFILE) until it matches EXPECTED: either a local file with the
# exact bytes or SHA256:BYTES. A fresh upload can briefly serve an error body
# instead of the asset, so a mismatch is retried with backoff before it fails.
set -euo pipefail
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}" "${FORGEJO_SERVER_URL:?FORGEJO_SERVER_URL is required}"

tag=${1:-} name=${2:-} expected=${3:-} destination=${4:-}
[[ -n $tag && -n $name && -n $expected && -n $destination && $# == 4 ]] || {
    echo "usage: $0 TAG NAME EXPECTED_FILE|SHA256:BYTES DESTINATION" >&2
    exit 2
}
if [[ -f $expected ]]; then
    expected_sha256=$(sha256sum -- "$expected" | awk '{print $1}')
    expected_bytes=$(stat -c '%s' -- "$expected")
else
    expected_sha256=${expected%%:*}
    expected_bytes=${expected#*:}
fi
[[ -d ${FJ_READ_PROFILE:-} ]] || { echo 'FJ_READ_PROFILE must name the read fj profile' >&2; exit 2; }
[[ $expected_sha256 =~ ^[0-9a-f]{64}$ && $expected_bytes =~ ^[0-9]+$ ]] || {
    echo "invalid expected digest for $name" >&2
    exit 2
}
delay=1
for attempt in 1 2 3 4 5 6; do
    rm -f -- "$destination"
    if XDG_DATA_HOME="$FJ_READ_PROFILE" fj -H "${FORGEJO_SERVER_URL#https://}" release asset download -r "$GITHUB_REPOSITORY" "$tag" "$name" -o "$destination" >/dev/null 2>&1 &&
        [[ $(stat -c '%s' -- "$destination") == "$expected_bytes" &&
            $(sha256sum -- "$destination" | awk '{print $1}') == "$expected_sha256" ]]; then
        echo "read back $name sha256=$expected_sha256 bytes=$expected_bytes"
        exit 0
    fi
    echo "read back $name did not match yet (attempt $attempt)" >&2
    sleep "$delay"
    delay=$((delay * 2))
done
echo "retained $name differs from the tested release bytes" >&2
exit 1
