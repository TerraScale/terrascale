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
an internal proxy, or a sibling checkout. The vendored SDK retains the downstream regional hostname correction
described below; the module pin still identifies the unpatched upstream archive.

To refresh, update both SDK versions in `go.mod`, run `GOFLAGS=-mod=mod go mod
tidy` and `GOFLAGS=-mod=mod go mod vendor`, then restore this file and record
the new commit and checksums. Run `make check-modules`, lint, and the fast and
slow tests before shipping. Builds continue to use `GOFLAGS=-mod=vendor`.

## Regional API hostname correction

The retained SDK snapshot has a downstream hostname patch: regional discovery,
region-catalog validation and strict pin construction use
`https://<region>.api.terrascale.tech`, including `https://gru.api.terrascale.tech`.
The source comments and README describe that same origin. `go.mod`, `go.sum`
and `vendor/modules.txt` retain the actual upstream version and zip checksum;
those identify the unpatched upstream module, not these vendor bytes.

The current dashboard callers require this pinned SDK surface. The native SDK
migration is tracked separately; this patch only corrects the regional origin.
The patch changes `regionalHostSuffix` from `.terrascale.tech` to
`.api.terrascale.tech` and replaces `<region>.terrascale.tech` in comments/docs.
No other vendored source is changed. SHA-256 values make the patch auditable:

| File under `vendor/terrascale.tech/sdk` | Before | After |
| --- | --- | --- |
| `discovery.go` | `5285b3afb822d96631282b9eaae134a28a2b96aa01e645cfc67358cd95f50b9b` | `3bd30de452b7db270e110181f759ca267e856d35ee7abe2176477e67e29367e1` |
| `client.go` | `940d083b91346992dfa3767593cab42fe6ea62232dea7a4850629290d770fda6` | `049b59d2054bb44b52a1ae1438dabc7b8c3c4e769847879fce976ea528358583` |
| `README.md` | `7ea2ff268d734b5bf56f4511a0ef821f1fb49814df7af261e5d35bbc66c63ed5` | `0be1c66dbc624994a148c829daa3e25f18fd27e157840e0012fe63dd8b4be29c` |
