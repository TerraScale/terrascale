#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 1 ]] || {
  echo "usage: deploy/release/sign.sh <release-output-directory>" >&2
  exit 2
}
output=$1
[[ -d "$output" && ! -L "$output" ]] || {
  echo "release output is not a directory: $output" >&2
  exit 1
}
[[ -f "$output/SHA256SUMS" && ! -L "$output/SHA256SUMS" ]] || {
  echo "release output has no regular SHA256SUMS" >&2
  exit 1
}
[[ ! -e "$output/SHA256SUMS.sig" && ! -L "$output/SHA256SUMS.sig" ]] || {
  echo "release output is already signed" >&2
  exit 1
}

work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-sign.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-sign.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT
umask 077

private_key=$work/private-key.pem
public_key=$work/public-key.pem
if [[ -n ${TERRABASE_RELEASE_SIGNING_KEY_FILE-} ]]; then
  install -m 0600 "$TERRABASE_RELEASE_SIGNING_KEY_FILE" "$private_key"
elif [[ -n ${TERRABASE_RELEASE_SIGNING_KEY_PEM-} ]]; then
  printf '%s\n' "$TERRABASE_RELEASE_SIGNING_KEY_PEM" >"$private_key"
else
  echo "protected release signing key is not configured" >&2
  exit 1
fi
if [[ -n ${TERRABASE_RELEASE_VERIFY_KEY_FILE-} ]]; then
  install -m 0600 "$TERRABASE_RELEASE_VERIFY_KEY_FILE" "$public_key"
elif [[ -n ${TERRABASE_RELEASE_VERIFY_KEY_PEM-} ]]; then
  printf '%s\n' "$TERRABASE_RELEASE_VERIFY_KEY_PEM" >"$public_key"
else
  echo "independent release verification key is not configured" >&2
  exit 1
fi

openssl pkey -in "$private_key" -text_pub -noout | grep -Fq 'ASN1 OID: prime256v1' || {
  echo "release signing key must be ECDSA P-256" >&2
  exit 1
}
openssl pkey -pubin -in "$public_key" -text_pub -noout | grep -Fq 'ASN1 OID: prime256v1' || {
  echo "release verification key must be ECDSA P-256" >&2
  exit 1
}

(
  cd "$output"
  sha256sum --check --strict SHA256SUMS
  openssl dgst -sha256 -sign "$private_key" -out SHA256SUMS.sig SHA256SUMS
  openssl dgst -sha256 -verify "$public_key" -signature SHA256SUMS.sig SHA256SUMS
)
chmod 0644 "$output/SHA256SUMS.sig"
echo "signed and verified $output/SHA256SUMS"
