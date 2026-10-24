# Encryption and deletion-rewrite integration

PR #464 rebases onto main `c1a1d9662662698739750a9af45db979abb2cea7`, including deletion authority and history erasure (#495), cut-log refill confirmation (#507), write-intent sync changes (#537), and the newer object-store and wire paths. CI at the preceding encryption head `ca07def2d` passed; that result and earlier local proofs do not qualify this rebase.

The publication conflict retains main's purge-fence check before moving either the wrapped key or its database into place. Rebuild salvage retains main's owned-operation lifetime, purge fencing and failed-close accounting. Reading and unwrapping its key occurs inside that lifetime, outside the blocking worker; copying and native reads still run on the worker.

A healthy rewrite copies and verifies encrypted pages using the current database key. It keeps the existing wrapped-key sidecar in place across the single atomic database rename. Rotating that key separately would let a crash leave a file paired with the wrong key. Separate copies and restores still generate fresh data keys. Rewrite log cleanup excludes the key, and failed pre-rename scratch cleanup retains the original database and key.

The recovery union retains both current-intent identity/version checks and main's verified-erasure receipt before clearing an intent. Stale/replaced work cannot clear a replacement or release its reservation; unverified erasure remains pending.

The healthy-rewrite fixtures open encrypted files with the existing test master. Their pre-rewrite marker check now reads decrypted CDC after-images: ciphertext never exposes that plaintext even before erasure. They still scan physical files for plaintext absence, assert the marker leaves CDC, preserve surviving logical state, and check the wrapped-key record is byte-for-byte unchanged. Raw ciphertext scans alone do not prove the absence of deleted plaintext in decrypted free pages. Existing failure-point/restart and peer-refill tests remain in the suite. Original shutdown workload, assertions and budgets are unchanged.

Storage format 17 remains provisional for CI, reserving 16 for #469. The #469-first queue hold remains; after it merges, rebase and check the actual main version plus one, then require current-head CI before queueing. No production reset, activation, credentials or native release changes are included. No local build/test or benchmark was run for this rebase; GitHub CI must validate it.
