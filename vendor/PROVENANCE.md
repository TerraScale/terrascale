# Vendored dependencies

`terrascale.tech/sdk` is the first-party [ts-go repository](https://git.terrascale.tech/TerraScale/ts-go)
at commit `430cb6624fa85ab22effc1a28d718db7d11297e1` (commit time
2026-09-30T02:31:59Z). Its `require` and public-repository `replace` in `go.mod`
pin `v0.0.0-20260930023159-430cb6624fa8`.

The public repository module ZIP checksum recorded in `go.sum` is
`h1:Mt4n4uAGYCBld4TRX0UDpXBjr70DYI9Z4gjIAIqc9zs=`; the module file checksum is
`h1:CBgIEfw0wTY2UDGAo1hmt1COj0872PnfTUNB3o0/3io=`. Go verifies these through
the public checksum database. This commit does not contain a LICENSE, NOTICE,
or COPYING file. Third-party licenses copied by `go mod vendor` remain
alongside their packages.

The vanity import has no Go module metadata, so the versioned replacement
resolves the source through the public HTTPS repository without credentials,
an internal proxy, or a sibling checkout. The vendored SDK source matches the
previous dashboard pin; this changes dependency resolution, not its API contract.

To refresh, update both SDK versions in `go.mod`, run `GOFLAGS=-mod=mod go mod
tidy` and `GOFLAGS=-mod=mod go mod vendor`, then restore this file and record
the new commit and checksums. Run `make check-modules`, lint, and the fast and
slow tests before shipping. Builds continue to use `GOFLAGS=-mod=vendor`.
