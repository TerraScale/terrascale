# CLI releases and installation

The CLI lives in `cmd/ts` in **TerraScale/ts-go**. The canonical installer is
`scripts/install.sh`; the landing site's `/install.sh` redirects to the raw
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
checks SHA-256 before extracting only `ts`, installs to `~/.local/bin` without
sudo, and preserves an existing binary if downloading or validation fails.
Checksums detect corruption; trust still depends on GitHub's HTTPS delivery
of the archive and checksum manifest.

Windows users download the matching zip from
<https://github.com/TerraScale/ts-go/releases>, verify its SHA-256 against
`checksums.txt` (PowerShell: `Get-FileHash .\ts_windows_amd64.zip -Algorithm SHA256`),
extract `ts.exe` to a user-owned directory and add that directory to PATH.

Every tag has these fixed asset names:

| Platform | amd64 | arm64 |
| --- | --- | --- |
| Linux | `ts_linux_amd64.tar.gz` | `ts_linux_arm64.tar.gz` |
| macOS | `ts_darwin_amd64.tar.gz` | `ts_darwin_arm64.tar.gz` |
| Windows | `ts_windows_amd64.zip` | `ts_windows_arm64.zip` |

Other assets: `checksums.txt`, `install.sh` and the generated Homebrew `ts.rb`.
`ts version` and `ts --version` print the embedded version; source builds print
`ts dev`. Snapshot packages print `ts 0.0.0-dev`.

## Release

1. Merge the reviewed PR after CI is green on its exact head, then create and
   push the next unused **v0.0.X** tag on a main commit.
2. The tag workflow builds all six targets and publishes the GitHub release
   with its own `GITHUB_TOKEN`; no registry token is needed.
3. Confirm the release assets, checksums and installed `ts --version`.

PRs run `goreleaser release --snapshot --clean` without credentials, validate
archives/install behavior with `python3 scripts/test-release.py`, and retain
packages for three days. This does not run or download TerraBase.

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
