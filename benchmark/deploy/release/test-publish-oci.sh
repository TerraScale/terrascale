#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-publish-fixtures.XXXXXXXX")
cleanup() {
  rm -rf -- "$work"
}
trap cleanup EXIT
mkdir -p "$work/bin" "$work/package"
cat >"$work/bin/skopeo" <<'FAKE_SKOPEO'
#!/usr/bin/env bash
set -euo pipefail
case "$1" in
  login) cat >/dev/null ;;
  copy) : ;;
  inspect)
    if [[ " $* " == *" docker://"* && ${FAKE_REGISTRY_MISMATCH-} == 1 ]]; then
      printf '{"schemaVersion":3}\n'
    else
      printf '{"schemaVersion":2}\n'
    fi
    ;;
  *) exit 2 ;;
esac
FAKE_SKOPEO
chmod 755 "$work/bin/skopeo"
printf 'fixture OCI bytes\n' >"$work/package/terrabase-server.oci.tar"
oci_digest=$(sha256sum "$work/package/terrabase-server.oci.tar" | awk '{print $1}')
jq -n --arg oci_digest "$oci_digest" '{
  package:"terrabase-server",
  version:"1.2.3",
  target:"x86_64-unknown-linux-gnu",
  source:{revision:"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},
  artifacts:{ociArchive:{name:"terrabase-server.oci.tar",sha256:$oci_digest}}
}' >"$work/package/terrabase-server.release.json"
printf '%s  %s\n' "$oci_digest" terrabase-server.oci.tar >"$work/package/SHA256SUMS"
openssl ecparam -name prime256v1 -genkey -noout -out "$work/signing-key.pem"
openssl ec -in "$work/signing-key.pem" -pubout -out "$work/verify-key.pem" >/dev/null 2>&1
openssl dgst -sha256 -sign "$work/signing-key.pem" \
  -out "$work/package/SHA256SUMS.sig" "$work/package/SHA256SUMS"

export PATH="$work/bin:$PATH"
export TERRABASE_RELEASE_REGISTRY=registry.example.test/terrascale
export TERRABASE_RELEASE_REGISTRY_USERNAME=publisher
export TERRABASE_RELEASE_REGISTRY_TOKEN=secret-fixture-token
export TERRABASE_RELEASE_VERIFY_KEY_FILE=$work/verify-key.pem
"$script_dir/publish-oci.sh" "$work/package" >/dev/null

if FAKE_REGISTRY_MISMATCH=1 "$script_dir/publish-oci.sh" "$work/package" >/dev/null 2>&1; then
  echo "registry manifest mismatch unexpectedly succeeded" >&2
  exit 1
fi
if TERRABASE_RELEASE_REGISTRY=registry.example.test/terrascale:latest \
  "$script_dir/publish-oci.sh" "$work/package" >/dev/null 2>&1; then
  echo "tagged registry destination unexpectedly succeeded" >&2
  exit 1
fi
if TERRABASE_RELEASE_REGISTRY=registry.example.test/terrascale@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa \
  "$script_dir/publish-oci.sh" "$work/package" >/dev/null 2>&1; then
  echo "digest-qualified registry destination unexpectedly succeeded" >&2
  exit 1
fi
TERRABASE_RELEASE_REGISTRY=registry.example.test:5000/terrascale \
  "$script_dir/publish-oci.sh" "$work/package" >/dev/null
if TERRABASE_RELEASE_REGISTRY_TOKEN= \
  "$script_dir/publish-oci.sh" "$work/package" >/dev/null 2>&1; then
  echo "missing registry token unexpectedly succeeded" >&2
  exit 1
fi
cp "$work/package/terrabase-server.oci.tar" "$work/original.oci.tar"
printf 'post-signature mutation\n' >>"$work/package/terrabase-server.oci.tar"
if "$script_dir/publish-oci.sh" "$work/package" >/dev/null 2>&1; then
  echo "post-signature OCI mutation unexpectedly succeeded" >&2
  exit 1
fi
cp "$work/original.oci.tar" "$work/package/terrabase-server.oci.tar"

echo "registry publication boundary fixtures passed"
