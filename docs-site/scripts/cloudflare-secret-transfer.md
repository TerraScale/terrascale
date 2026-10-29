# Move the existing scoped docs CI credential

Do not copy the host's broader `cfut_` user token into Actions. The old repository
already has a scoped `CLOUDFLARE_API_TOKEN`; transfer those same bytes without
printing them or changing its permissions.

The root rule says credential installs are one-command handoffs the user runs.
The parent may prepare an encrypted copy, but the final installer below is a
user action. No new Cloudflare token, token scope or GitHub permission is needed.

Create a temporary 0600 RSA private key outside every repository and derive its
public key:

```bash
umask 077
transfer_dir=$(mktemp -d)
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$transfer_dir/private.pem"
openssl pkey -in "$transfer_dir/private.pem" -pubout -out "$transfer_dir/public.pem"
gh variable get CLOUDFLARE_ACCOUNT_ID --repo TerraScale/ts-docs > "$transfer_dir/account-id"
```

Ship `cloudflare-secret-export.yml.template` as a temporary
`.github/workflows/cloudflare-secret-export.yml` in the old `ts-docs` repo, along
with only the public key as `cloudflare-transfer-public.pem`. Follow the normal
`ts-ship` path. The template has `workflow_dispatch`, `contents: read`, and uses
the existing `CLOUDFLARE_API_TOKEN` secret only in this encryption step:

```bash
printf '%s' "$CLOUDFLARE_API_TOKEN" |
  openssl pkeyutl -encrypt -pubin -inkey transfer-public.pem \
    -pkeyopt rsa_padding_mode:oaep -pkeyopt rsa_oaep_md:sha256 > cloudflare-token.enc
```

Dispatch the temporary workflow. It uploads only `cloudflare-token.enc` under
`encrypted-cloudflare-token`, with one-day retention. Download that artifact
into the same private transfer directory. Preserve the encrypted artifact,
0600 private key and `account-id` in the parent's private backup until the user
runs the handoff; this makes the old repo's deletion independent of a later
credential install.

The user's complete credential handoff is one command from the monorepo root:

```bash
bash docs-site/scripts/install-cloudflare-ci-token.sh /absolute/private/transfer-directory
```

The script decrypts into a temporary 0600 file, rejects empty output, pipes the
unchanged token into `gh secret set`, installs the saved nonsecret account
variable, and removes the plaintext on exit. It never prints the token or puts
it in an argument.

Remove the temporary workflow/public key in the old repo, delete its encrypted
artifact, and remove the local temporary key directory after the handoff succeeds. Include the final old
repo commits in the mirror bundle before deletion. Verify the monorepo
`docs-deploy.yml` run and `https://docs.terrascale.tech/build-info.json` before
retiring the old repository.

## Live proof without moving host credentials

After the exact main commit is green and has a `docs/qualified-<sha>` artifact,
run this on a dev host through the existing heavy runner:

```bash
ts-heavy-any python3 docs-site/scripts/deploy-current-main.py --repo terrascale --branch main
```

This uses the host's existing `cf` authentication, requires the source revision
and artifact hashes to match current green main, publishes the prebuilt site,
checks the hosted pages/assets and build-info SHA, and records `docs-live/docs`
on that commit. It neither installs credentials nor changes token permissions.
