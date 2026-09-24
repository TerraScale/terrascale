# Vendored dependencies

`terrascale.tech/sdk` is the first-party `ts-go` repository at commit
`3088ef2e3bd0cfbd22c980c029b2b745b82d1eff` (commit time
2026-09-23T16:17:29Z). The module is pinned in `go.mod` as
`v0.0.0-20260923161729-3088ef2e3bd0`; its module zip checksum in `go.sum` is
`h1:h/OoGNKjp4+e6Py657PH2xO1TEaN8/PBpl94C+XjsXw=`. The source was taken
from a clean checkout of `ssh://git@git.terrascale.tech/TerraScale/ts-go.git`.
That repository did not contain a LICENSE, NOTICE, or COPYING file at this
commit. Third-party licenses copied by `go mod vendor` remain alongside their
packages.

The `ts-go` vanity import currently has no publicly resolvable Go module
version. To refresh this snapshot, first make the chosen `ts-go` commit
available to Go as a versioned module through an internal proxy (or publish the
module and its `go-import` metadata), update `go.mod`, then run `go mod tidy`
and `go mod vendor`. Verify the new commit, module checksum, and an offline
`GOFLAGS=-mod=vendor GOPROXY=off go test ./...` build before updating this file.
