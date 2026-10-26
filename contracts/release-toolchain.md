# CLI compiler and advisory checks

The SDK language and consumer floor remains Go 1.25.0 in `go.mod`. The release
compiler is independently pinned to Go 1.27.3 in `.go-version`; `GOTOOLCHAIN=local`
prevents an implicit compiler replacement. The Go download catalog listed
1.27.3 as the latest stable version on 2026-10-26.

The existing test and release jobs cache only the pinned govulncheck v1.9.0
executable, never advisory results. Its Go 1.27.3 trimpath build is checksum-verified
on every use and self-scanned; only trusted-main CI saves the scanner cache.
Every ready PR (including documentation-only changes) queries the current Go advisory
feed. Automatic discovery covers every committed Go module; module scans cover the SDK and CLI dependency graphs and fail for
any known advisory, regardless of severity or source reachability. Source symbol
analysis covers every package and includes tests. Dependency manifests and the consumer floor are unchanged.

GoReleaser runs `scripts/check-release-binary.sh` on each Linux, macOS and
Windows amd64/arm64 binary before packaging or publication. The hook checks the
embedded compiler using `go version -m`, then runs native module and package
advisory scans. Module scanning covers stripped binaries even when symbol-level
reachability is unavailable. A finding stops packaging and release. Existing
archive checksum, installer and native snapshot checks remain in place.

The test job separately compiles an isolated consumer importing all three public
SDK packages under Go 1.25.0. This verifies source compatibility at the supported
floor; it does not qualify that old compiler for shipping CLI executables.

To qualify an unsigned candidate without publication:

```sh
# Install Go from .go-version and govulncheck v1.9.0 first; add it to PATH.
nice -n 19 goreleaser release --snapshot --clean --skip=sign,publish
nice -n 19 python3 scripts/test-release.py
```

Snapshot checks execute only the candidate built from the checked-out source.
Inspect downloaded public binaries with `go version -m` and govulncheck binary
mode without executing them. Successful snapshots do not authorize tags,
signing, publication or changes to release approval holds. Previously published
assets are unchanged until a separately authorized release replaces them.
