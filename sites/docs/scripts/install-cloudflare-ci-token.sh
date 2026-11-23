#!/usr/bin/env bash
# User-run handoff: reuse the old docs CI token without broadening its scope.
set -euo pipefail
if [[ $# != 1 ]]; then
  echo 'usage: install-cloudflare-ci-token.sh <private-transfer-directory>' >&2
  exit 2
fi
transfer_dir=$1
private_key="$transfer_dir/private.pem"
ciphertext="$transfer_dir/cloudflare-token.enc"
account_file="$transfer_dir/account-id"
for file in "$private_key" "$ciphertext" "$account_file"; do
  [[ -f "$file" && ! -L "$file" && -O "$file" ]] || { echo 'Missing or unsafe transfer file.' >&2; exit 1; }
done
[[ $(stat -c %a "$private_key") = 600 ]] || { echo 'The transfer private key must have mode 0600.' >&2; exit 1; }
account=$(tr -d '\r\n' < "$account_file")
[[ "$account" =~ ^[a-f0-9]{32}$ ]] || { echo 'Invalid Cloudflare account ID in transfer.' >&2; exit 1; }
umask 077
plaintext=$(mktemp)
trap 'rm -f "$plaintext"' EXIT
openssl pkeyutl -decrypt -inkey "$private_key" \
  -pkeyopt rsa_padding_mode:oaep -pkeyopt rsa_oaep_md:sha256 \
  -in "$ciphertext" -out "$plaintext"
[[ -s "$plaintext" ]] || { echo 'The encrypted credential was empty.' >&2; exit 1; }
gh secret set CLOUDFLARE_API_TOKEN --repo TerraScale/terrascale < "$plaintext"
printf '%s' "$account" | gh variable set CLOUDFLARE_ACCOUNT_ID --repo TerraScale/terrascale
echo 'Installed the existing scoped docs CI token and account variable in terrascale.'
