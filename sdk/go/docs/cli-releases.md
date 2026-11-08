# CLI releases and installation

The CLI lives in `sdk/go/cmd/ts` in **TerraScale/terrascale**. Run packaging commands from `sdk/go`. The canonical installer is
`sdk/go/scripts/install.sh`; the landing site's `/install.sh` redirects to the raw
file on this repository's main branch. No second installer copy is needed.

## Install a published release

```sh
curl --proto '=https' --tlsv1.2 -fsSL https://terrascale.tech/install.sh -o install.sh
sh install.sh                    # latest 0.0.X release
# Or: sh install.sh 0.0.1
export PATH="$HOME/.local/bin:$PATH"
ts --version
```

The installer supports Linux and macOS, amd64/x86_64 and arm64/aarch64. It
pages through all monorepo releases to find the highest stable Go version,
checks SHA-256 before extracting only `ts`, installs to `~/.local/bin` without
sudo, and preserves an existing binary if downloading or validation fails.
Checksums detect corruption; trust still depends on GitHub's HTTPS delivery
of the archive and checksum manifest.

Windows users download the matching zip from
<https://github.com/TerraScale/terrascale/releases>, verify its SHA-256 against
`checksums.txt` (PowerShell: `Get-FileHash .\ts_windows_amd64.zip -Algorithm SHA256`),
extract `ts.exe` to a user-owned directory and add that directory to PATH.

Every `sdk/go/v0.0.X` tag has these fixed asset names:

| Platform | amd64 | arm64 |
| --- | --- | --- |
| Linux | `ts_linux_amd64.tar.gz` | `ts_linux_arm64.tar.gz` |
| macOS | `ts_darwin_amd64.tar.gz` | `ts_darwin_arm64.tar.gz` |
| Windows | `ts_windows_amd64.zip` | `ts_windows_arm64.zip` |

Other assets: `checksums.txt`, `install.sh` and the generated Homebrew `ts.rb`.

The original `0.0.2`–`0.0.10` CLI downloads retain the `history/ts-go/v0.0.X` release namespace in the monorepo. The installer resolves explicitly requested old versions there; newer versions use `sdk/go/v0.0.X`. Module tags always use `sdk/go/v0.0.X`, with each historical module source tree preserved unchanged.
`ts version` and `ts --version` print the embedded version; source builds print
`ts dev`. Snapshot packages print `ts 0.0.0-dev`.

## Release

Every green main push that touches `sdk/go/**`, `scripts/sdk/**` or the Go release
and publish workflows releases the CLI automatically; there is no manual tag step and no
deploy switch. The path is:

1. Push the change to main; SDK Go Fast runs in GitHub Actions.
2. The `SDK Go Release` workflow fires on every main push that touches the Go
   SDK paths. It waits for a green main revision — the pushed commit, or the
   newest unreleased ancestor since the last `sdk/go/v0.0.X` tag. Its
   `autorelease` job runs `python3 scripts/sdk/build-release.py go`: the release
   gates, all six GoReleaser targets (version = highest existing
   `sdk/go/v0.0.X` tag + 1) and `python3 scripts/test-release.py`. It then
   creates the `sdk/go/v0.0.X` tag on that exact revision. Queue delays and busy main
   sections heal on the next trigger; an already-released version is skipped.
3. A tag created with the job token starts no workflow, so the same run then
   calls `SDK Go Publish` for the new tag. Publish also runs on a pushed
   `sdk/go/v0.0.X` tag and by dispatch with a `tag` input. It verifies the tag
   points into main, takes the bytes autorelease built (a tag push or dispatch
   rebuilds them from the tag) and publishes them as the release assets with the workflow's `GITHUB_TOKEN`. A failed publish is rerun from its
   job or dispatched for the tag. This requires no paid GoReleaser monorepo support.
4. Confirm the release assets, checksums and installed `ts --version`. The
   homebrew-tap syncs the new `ts.rb` on its own 15-minute schedule.

Credential-free package checks run `goreleaser release --snapshot --clean`, validate archives/install behavior with `python3 scripts/test-release.py`, and retain packages for three days. This does not run or download TerraBase. For release packaging set `TS_GO_VERSION` to the numeric `0.0.X` version; the default snapshot version stays `0.0.0-dev`.

## Homebrew

```sh
brew install terrascale/tap/ts
```

Each release generates `ts.rb` as a release asset. The pinned GoReleaser still
supports `brews`; it is kept because formulas cover Linux and macOS, whereas
casks only cover macOS. **TerraScale/homebrew-tap** pulls the formula itself:
its sync workflow runs every 15 minutes, checks that every URL and SHA-256 in
`ts.rb` matches the latest release's `checksums.txt`, and commits
`Formula/ts.rb` with the tap's own token. To publish sooner, run it by hand:

```sh
gh workflow run sync.yml --repo TerraScale/homebrew-tap
```
