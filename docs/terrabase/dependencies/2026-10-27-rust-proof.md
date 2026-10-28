# Rust proof dependency resolution

The `libsql` 0.10.0 client cannot resolve patched Rustls/WebPKI versions and depends
on the vulnerable `libsql-sqlite3-parser` 0.13.0, which has no published fix.
The Rust proof now uses upstream `libsql-hrana` 0.10.0 wire types and reqwest
0.13.5 for verified HTTPS, with redirects disabled. The locked graph selects
Rustls 0.23.46 and WebPKI 0.103.16 and contains no SQL parser dependency.

This is a protocol proof rather than qualification of the full Rust libsql SDK.
It keeps positional and named parameters, a transactional batch, joins,
read-your-writes, commit/rollback and owned-table cleanup. JS, Go and Python
still exercise their official clients. Remote URLs require HTTPS; HTTP is
limited to loopback fixtures. Credentials remain in the existing token file.

`make terrabase-libsql-rust-check` checks formatting and compiles in locked mode
with Rust 1.99.1, the stable release published on 2026-10-16. CI requires it
when the proof or its workflow/build recipe changes; only main saves registry
downloads. Dependabot includes the proof workspace in its Cargo configuration.
The source was adopted from orphaned commit `c311d8d14`, retaining authorship.

No server API, storage format or production authentication behavior changes.
Current-source compile and CI results are recorded in the PR; the previous
SDK script's production result does not qualify the replacement transport.
