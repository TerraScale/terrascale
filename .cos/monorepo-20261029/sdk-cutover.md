# SDK monorepo cutover

All five SDK repositories were imported with full history into public
`TerraScale/terrascale`, verified, backed up on delta, and deleted from GitHub.
The final independent API check returned HTTP 404 for all five, and none appeared
in the TerraScale organization listing. Docs and dashboard have separate owners.

SDK import and Fast qualification: `a6abf732a7852481eb2f5d6b148803699fef8606`.
Platform source consumption: `e13a418b00385efd768f7a51645a3f9581ca6259`.

| Source repo | Monorepo path | Source commits / tags | Preserved refs | Issues / releases / assets moved | Fast seconds | Source status |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| ts-dotnet | sdk/dotnet | 684 / 11 | 143 | 0 / 0 / 0 | 125.265 | Deleted, HTTP 404 |
| ts-js | sdk/js | 738 / 14 | 94 | 0 / 0 / 0 | 37.120 | Deleted, HTTP 404 |
| ts-go | sdk/go | 523 / 10 | 182 | 0 / 9 / 81 | 11.972 | Deleted, HTTP 404 |
| ts-python | sdk/python | 91 / 4 | 44 | 0 / 2 / 0 | 10.645 | Deleted, HTTP 404 |
| ts-rust | sdk/rust | 392 / 3 | 150 | 0 / 0 / 0 | 110.152 | Deleted, HTTP 404 |

Each import's subtree tree equals the saved source-main tree before integration
edits. Original commits remain ancestors of main. Source branches and exact tag
objects are preserved under `history/<repo>/`; other refs use `refs/archive/<repo>/`.
Release assets were hash-verified, and source metadata was captured before deletion.

The five SDK Fast workflows passed on main:
[.NET](https://github.com/TerraScale/terrascale/actions/runs/40974286151),
[JavaScript](https://github.com/TerraScale/terrascale/actions/runs/40974286079),
[Go](https://github.com/TerraScale/terrascale/actions/runs/40974285974),
[Python](https://github.com/TerraScale/terrascale/actions/runs/40974286075), and
[Rust](https://github.com/TerraScale/terrascale/actions/runs/40974286022).
SDK Fast and release workflows are area-scoped, with the existing package identities.
Registry credentials and first publication remain the explicit
[registry handoff](registry-handoff.md), which does not block source retirement.

The root platform now references `sdk/dotnet`, including Docker build contexts and
CI. It no longer checks out the deleted .NET repository or uses its deploy key.
SDK and server dependency graphs remain independent. Exact-SHA Fast proofs for
`e13a418b0`: libs 74.786s, TerraBase 255.502s, .NET SDK 138.161s, Auth 125.791s
(105 tests passed, no failures). Auth ran separately after concurrent cold builds
exceeded its deadline; the other successful exact-SHA proofs were reused.

## Go serving and consumers

`https://terrascale.tech/sdk?go-get=1` serves:

```html
<meta name="go-import" content="terrascale.tech/sdk git https://github.com/TerraScale/terrascale sdk/go">
```

Go 1.27.3 with `GOPROXY=direct`, a fresh module cache, and the default checksum
database resolved `terrascale.tech/sdk@v0.0.10`, `@v0.0.5`, and `@v0.0.1`.
Ten `sdk/go/v0.0.X` module tags preserve the original module content.
The [compatibility release](https://github.com/TerraScale/terrascale/releases/tag/sdk/go/v0.0.10)
contains nine assets; all six archives and checksums match the originals.
The live installer matches `sdk/go/scripts/install.sh`.
Homebrew workflow commit `42bb5b6` shipped and its successful sync updated
`Formula/ts.rb` to the monorepo release URLs with matching digests.

## Preservation on delta

Base: `/home/mariogk/Projects/TerraScale/.cos/monorepo-20261029/`.
Bundles are immutable files at
`snapshots/20261029T215006Z/<repo>/<repo>.bundle`.
All were restored, fsck-checked, digest-checked, and bundle-verified in a repository.

| Bundle | SHA-256 |
| --- | --- |
| ts-dotnet.bundle | f90cb33a8471494dd397f479a8260c2f277b4afe338410ca484e8c7567dd16f0 |
| ts-js.bundle | 3623c4c9f9852cf78d1ce42981a4269ee0799c65bc92c4dd8fc876f2dddc3bf3 |
| ts-go.bundle | f95217469a3ec8f842f90bd5b0644c3b44dd7adbbedbc1f5e97ae2b49b875c92 |
| ts-python.bundle | ecd6f324bb429f28c366b7044e258197437a601a0c9395843a27a722cc14ed98 |
| ts-rust.bundle | 9024bc4f35c9f029ee342289cb3ff20a77b61cde83b8392cd0271dbe9c14f430 |

`evidence/<repo>/` holds migration, ref/tree verification, release/asset hashes,
final metadata and deletion proofs. Go additionally has `summary.json` and live
module, installer and tap proofs. `evidence/platform-sdk-cutover.json` records the
platform qualification and `evidence/sdk-final-deletion-proof.json` records the
final independent five-repo deletion check.

`ts-repos` and `ts-sync-rules` discover live GitHub repositories and therefore omit
the deleted SDK repositories automatically. `ts-ship` maps `sdk-<lang>` to
`sdk/<lang>/` and `make sdk-<lang>-test-fast`; its owner received the final retirement
mapping and request to remove the obsolete standalone preparations.
