# Backup capture provenance producer slice

Applied the approved producer-side provenance wiring:

- routed participant captures now mark their `BackupPlan` as
  `RoutedShardParticipant` immediately before execution;
- envelope assembly rejects any participant manifest that is not explicitly
  `RoutedShardParticipant`;
- envelope fixture manifests carry the routed capture kind, so existing
  envelope round-trip coverage exercises the new invariant.
- retention and provider-failure envelope fixtures now generate their member
  archives with a routed snapshot plan; they continue to exercise remote
  deletion/restart and all-provider atomic publication paths.

The standalone whole-database producer remains the constructor default owned
by the backup-plan/manifest implementation. No outer encryption envelope,
object key, or compatibility path was changed.

Validation performed:

```text
rustfmt --edition 2024 --check src/backup/worker.rs src/backup/envelope.rs
```

Cargo compile/tests remain assigned to the consolidated owner; sibling
manifest/plan changes are present in the shared worktree and are intentionally
not modified here.
