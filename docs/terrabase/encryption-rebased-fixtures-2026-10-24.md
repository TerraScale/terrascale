# Encryption fixture integration after deletion recovery changes

CI run [40056594140](https://github.com/TerraScale/terrascale/actions/runs/40056594140) compiled head `d2099b463` and passed all 44 vendor cases. Four Fast cases failed: two replacement-intent startup expectations, a replication progress count, and a plaintext positive sentinel in encrypted storage.

Startup now defers an intent still present after a recovery attempt to convergence. The replacement tests allow startup to succeed, freeze the convergence clock, verify that startup did not revisit the replacement, and retain the committed replacement catalog, source preservation, neighbor and released-directory-lock checks. The unchanged current-intent conflict test continues to require startup refusal.

The replicated drop pauses after each 512 units of work. `JoinAsync` increments transaction work once more after the final paused drop finishes. A heartbeat may report that strictly growing value before the commit ACK; the count is therefore not necessarily four at ACK time. The fixture checks that each held checkpoint receives progress once with no duplicate notification while its work is unchanged. It retains all deadline, stream identity, no-premature-ACK, persisted cursor and committed-drop checks.

The webhook replay test reads persisted SYSTEM documents through a keyed native connection for its positive live-neighbor sentinel and refused-payload absence. It also searches decrypted CDC before/after images for the refused payload and retains raw physical-file plaintext checks. Ciphertext scans do not prove absence from decrypted free pages; erasure certification, replay, tombstone and restart/repair checks remain independent assertions.

These changes affect test fixtures only. The [focused five-repeat proof](../../benchmark/bench/2026-10-24-encryption-rebased-fixtures.md) passed 55/55 at `839065c23`, including all four previously failing cases and recovery safety coverage. Full exact-head CI remains a separate gate; earlier passing evidence does not qualify this head.
