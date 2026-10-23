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

## Release handoff (owner only)

1. Merge the reviewed PR after CI is green on its exact head. Confirm the release
   commit is on current main and select the next unused **v0.0.X** tag.
2. Complete the Homebrew handoff below before pushing the tag. Only with
   release authorization, create and push that tag. The tag workflow
   builds all six targets and publishes the GitHub release with its own
   `GITHUB_TOKEN`; no registry token is needed. No tag is created by this PR.
3. Confirm the release assets, checksums and installed `ts --version` before
   advertising installation. There is no published CLI release yet.

PRs run `goreleaser release --snapshot --clean` without credentials, validate
archives/install behavior with `python3 scripts/test-release.py`, and retain
packages for three days. This does not run or download TerraBase.

## Homebrew handoff (held until tap authorization)

The release generates `ts.rb`; the dependent `homebrew` job publishes it to the
tap only after the GitHub release succeeds. The pinned GoReleaser
still supports `brews`; this deprecated integration is retained because
formulas cover both Linux and macOS, whereas casks only cover macOS.

After the owner authorizes and creates **TerraScale/homebrew-tap**, the owner
supplies a separate token limited to writing that tap using this single command
(interactive hidden prompt; never pass `--body` or put the token in chat):

```sh
gh secret set HOMEBREW_TAP_TOKEN --repo TerraScale/ts-go
```

The next authorized **v0.0.X** tag then publishes Homebrew automatically after
the GitHub release succeeds. The `homebrew` job validates the tag, downloads
`ts.rb` from that exact release and commits it to `Formula/ts.rb` in the tap.
It cannot create the tap; a missing tap or token fails the Homebrew job without
undoing the GitHub release. After correcting the prerequisite, re-run the
failed job for the same tag. No separate manual dispatch is needed.
Once published, installation is `brew install TerraScale/tap/ts`.
