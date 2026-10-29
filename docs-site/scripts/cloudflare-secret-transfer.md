# Move the existing scoped docs CI credential

Do not copy the host's broader `cfut_` user token into Actions. The old repository
already has a scoped `CLOUDFLARE_API_TOKEN`; transfer those same bytes without
printing them or changing its permissions.

After the parent authorizes the existing credential install, create a temporary
0600 RSA private key outside every repository and derive its public key:

```bash
umask 077
transfer_dir=$(mktemp -d)
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$transfer_dir/private.pem"
openssl pkey -in "$transfer_dir/private.pem" -pubout -out "$transfer_dir/public.pem"
```

Commit only the public key and a temporary workflow to the old `ts-docs` repo.
The workflow must use `workflow_dispatch`, `contents: read`, and the existing
`CLOUDFLARE_API_TOKEN` secret as an environment variable. Its export step is:

```bash
printf '%s' "$CLOUDFLARE_API_TOKEN" |
  openssl pkeyutl -encrypt -pubin -inkey transfer-public.pem \
    -pkeyopt rsa_padding_mode:oaep -pkeyopt rsa_oaep_md:sha256 > cloudflare-token.enc
```

Upload only `cloudflare-token.enc` with `actions/upload-artifact`. Download that
encrypted artifact locally, then perform the one-command credential handoff:

```bash
openssl pkeyutl -decrypt -inkey "$transfer_dir/private.pem" \
  -pkeyopt rsa_padding_mode:oaep -pkeyopt rsa_oaep_md:sha256 \
  -in cloudflare-token.enc |
  gh secret set CLOUDFLARE_API_TOKEN --repo TerraScale/terrascale
```

Copy the existing nonsecret account variable with:

```bash
gh variable get CLOUDFLARE_ACCOUNT_ID --repo TerraScale/ts-docs |
  gh variable set CLOUDFLARE_ACCOUNT_ID --repo TerraScale/terrascale
```

Remove the temporary workflow/public key in the old repo, delete its encrypted
artifact, and remove the local temporary key directory. Include the final old
repo commits in the mirror bundle before deletion. Verify the monorepo
`docs-deploy.yml` run and `https://docs.terrascale.tech/build-info.json` before
retiring the old repository.
