#!/usr/bin/env bash
# Auth's qualified executable and its qualified storage dependency; neither is the token-validation target.
set -euo pipefail
umask 077
[[ $(uname -s) == Linux && $(uname -m) == x86_64 ]] || { echo 'The real Auth fixture requires Linux/AMD64.' >&2; exit 1; }
destination=$1
mkdir -p "$destination"
if [[ ${CI:-} == true ]]; then
  : "${FORGEJO_TOKEN:?FORGEJO_TOKEN is required for the private fixture asset}"
  export XDG_DATA_HOME="$destination/fj-data" XDG_CONFIG_HOME="$destination/fj-config"
  printf '%s' "$FORGEJO_TOKEN" | fj -H git.terrascale.tech auth add-token >/dev/null
  unset FORGEJO_TOKEN
fi
curl --fail --silent --show-error --proto =https --connect-timeout 10 --max-time 30 --max-filesize 8192 \
	'https://git.terrascale.tech/v2/token?service=container_registry&scope=repository:terrascale/ts-auth:pull' -o "$destination/registry-token.json"
python3 - "$destination/registry-token.json" "$destination/registry-curl.conf" <<'PY'
import json, pathlib, re, sys
token = json.loads(pathlib.Path(sys.argv[1]).read_text())["token"]
if not re.fullmatch(r"[A-Za-z0-9._-]+", token):
    raise SystemExit("Registry returned an invalid anonymous token.")
pathlib.Path(sys.argv[2]).write_text('header = "Authorization: Bearer ' + token + '"\n')
PY
curl --config "$destination/registry-curl.conf" --fail --silent --show-error --proto =https \
	--connect-timeout 10 --max-time 30 --max-filesize 1065 -H 'Accept: application/vnd.oci.image.manifest.v1+json' \
	https://git.terrascale.tech/v2/terrascale/ts-auth/manifests/sha256:047b76d48cd4f36771ebfd856742de7ab5ecd1610e3f4e7ad0c304358065320a -o "$destination/manifest.json"
printf '%s  %s\n' 047b76d48cd4f36771ebfd856742de7ab5ecd1610e3f4e7ad0c304358065320a "$destination/manifest.json" | sha256sum --check --status
layer=07049c48a22f84c603cda6e0c037fefdb88e27ce95341433354299068b695f87
curl --config "$destination/registry-curl.conf" --fail --silent --show-error --proto =https \
	--connect-timeout 10 --max-time 120 --max-filesize 55672687 \
	"https://git.terrascale.tech/v2/terrascale/ts-auth/blobs/sha256:$layer" -o "$destination/auth-layer.tar.gz"
printf '%s  %s\n' "$layer" "$destination/auth-layer.tar.gz" | sha256sum --check --status
tar -xf "$destination/auth-layer.tar.gz" -C "$destination" ts-auth
[[ $(wc -c < "$destination/ts-auth") == 118315926 ]]
printf '%s  %s\n' c6e81b1d30aef2ac46a66c9eef0d421a4104a5a7e8574fd865f2a60efd73cabb "$destination/ts-auth" | sha256sum --check --status
(
	ulimit -f 110548 # The qualified storage binary is 113200335 bytes; bound fj's output to the next KiB.
	timeout --signal=TERM --kill-after=5s 120s fj release asset download -H git.terrascale.tech -r TerraScale/terrabase \
		ci-ff256067015433c8c40b59879f1282d2a40480ce-run-13258 terrabase-linux-amd64 --output "$destination/terrabase"
)
[[ $(wc -c < "$destination/terrabase") == 113200335 ]]
printf '%s  %s\n' e07d173fc2bbab19dcb1fde8b121b0207b07d55452a5b19a406871d220e05e13 "$destination/terrabase" | sha256sum --check --status
chmod +x "$destination/ts-auth" "$destination/terrabase"
