# Registry access after the monorepo import

Repository: `TerraScale/terrascale` (public). Package names and versions stay unchanged. Registry access does not block preserving and deleting the seven source repositories.

These steps install credentials only in GitHub Actions. Never paste a credential into chat, a command argument, a file in git, or a shell history entry. The `gh secret set` commands below prompt privately for the value.

## npm: move the existing trusted publisher

1. Sign in to npm as an owner of [`@terrascale/sdk`](https://www.npmjs.com/package/@terrascale/sdk/access).
2. Open the package's Settings / Trusted publishing. Replace the existing GitHub publisher with organization `TerraScale`, repository `terrascale`, workflow filename **`sdk-js-release.yml`**, no environment.
3. Keep the package public and its name `@terrascale/sdk`; the next source version is `0.0.18`.

No npm token is needed with the trusted publisher. The workflow publishes a previously qualified tarball with provenance after a tag such as `sdk/js/v0.0.18` reaches main.

## NuGet: first public TerraScale.DotNet publication

1. Sign in at [NuGet API keys](https://www.nuget.org/account/apikeys) with the account that will own `TerraScale.DotNet`.
2. Create a short-lived push API key permitting creation/publication of **`TerraScale.DotNet`** only. Copy it into the private prompt from this command:

   ```sh
   gh secret set NUGET_API_KEY --repo TerraScale/terrascale
   ```

The workflow is **`sdk-dotnet-release.yml`** and the current source package version is `0.0.20`; release tags use `sdk/dotnet/v0.0.20`. It verifies qualified package bytes through GitHub Packages before pushing those same bytes to nuget.org. Existing workflow support uses an API key; switching to NuGet trusted publishing requires separately updating that workflow's authentication.

## PyPI: register the first trusted publisher

1. Sign in to [PyPI pending publishers](https://pypi.org/manage/account/publishing/).
2. Add a GitHub pending publisher with project **`terrascale`**, owner **`TerraScale`**, repository **`terrascale`**, workflow filename **`sdk-python-release.yml`**, environment **`pypi`**.
3. No API token is required. The first source version is `0.0.5`, released with `sdk/python/v0.0.5`.

The GitHub workflow's environment name must remain `pypi`. A pending publisher reserves the publication relationship; the first successful trusted publication creates the project.

## crates.io: permit the first terrascale publication

1. Sign in at [crates.io API tokens](https://crates.io/settings/tokens) using the account that will own `terrascale`.
2. Create a short-lived token permitting the first publication of **`terrascale`** (new-crate publication if the token UI distinguishes it). Install it with:

   ```sh
   gh secret set CARGO_REGISTRY_TOKEN --repo TerraScale/terrascale
   ```

The workflow is **`sdk-rust-release.yml`**; source version `0.0.5` uses tag `sdk/rust/v0.0.5`. After first publication, restrict the replacement token to this crate.

## Release operator commands after access is installed

Start from current green `terrascale/main`. Qualify each package on a dev host, then tag that exact SHA. The qualification command stores immutable source-bound artifacts and its successful status; publication workflows reject missing or different-SHA qualification.

```sh
ts-heavy-any python3 scripts/sdk/qualify-release.py js --repo terrascale --branch main
ts-heavy-any python3 scripts/sdk/qualify-release.py dotnet --repo terrascale --branch main
ts-heavy-any python3 scripts/sdk/qualify-release.py python --repo terrascale --branch main
ts-heavy-any python3 scripts/sdk/qualify-release.py rust --repo terrascale --branch main
```

Create only the relevant `sdk/<lang>/v0.0.X` tag on the qualified SHA and push it. No source repository credentials or URLs are needed after cutover.

## Cloudflare: retain the existing docs deployment credential

The old `ts-docs` scoped token has been exported as RSA-OAEP ciphertext on delta, verified without displaying plaintext, and preserved outside git. No new credential or permissions are needed. The workspace rule requires the operator to install credentials. Run this exact command on **delta**:

```sh
bash /home/mariogk/Projects/TerraScale/.cos/monorepo-20261029/cloudflare-ci-transfer/install.sh /home/mariogk/Projects/TerraScale/.cos/monorepo-20261029/cloudflare-ci-transfer
```

It installs the existing token into `TerraScale/terrascale`'s `CLOUDFLARE_API_TOKEN` Actions secret through stdin, then copies the existing nonsecret account ID. The token is never printed. Delete the private transfer directory after the monorepo `docs-deploy.yml` succeeds. The old repository must remain until docs is live from the monorepo and this credential handoff is verified.
