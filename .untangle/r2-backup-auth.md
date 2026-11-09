# Round 2 untangle: backup-auth

Slice: `src/TerraBase/Features/{Backup,FileStorage,Functions,UserAuth,ApiKeys}` (Protos untouched),
tests `tests/TerraBase.{Fast,Slow}/Backup`, `tests/TerraBase.Fast/Functions`.

## Fixes

- Delete every comment in the slice (house rule 1); classify third-party constraints
  (S3/S3-compatible protocol behavior, ts-auth key rotation, IANA registries, crypto-shredding
  protocol ordering documented in docs/terrabase) as THIRD-PARTY below instead of keeping the
  comment text. Where a comment explains a pure code invariant that a name can carry, adjust the
  name; no information is moved into new names beyond that.
  - ~330 comment lines across 39 files (counts per `rg -c "^\s*(//|/\*|\*)"`), plus 4 trailing
    comments and 1 block comment.
- Break the Backup↔Management service-locator cycle with a narrow interface owned by Backup.
  - `Backup/BackupExtensions.cs:48` registers `Lazy<Management.EngineManagementService>` via
    `GetRequiredService`, and `Backup/KeyRevocationLocal.cs:8` takes `Lazy<EngineManagementService>`
    — a service-locator dependency on another feature's concrete class.
  - Fix: new internal `ShreddedDatabaseObserver` interface in Backup
    (`ObserveShreddedAsync`, `EraseShreddedAsync`), implemented by `EngineManagementService`
    (smallest possible edits in two Management files, listed below), registered as a forwarding
    singleton; `KeyRevocationLocal` takes the interface directly and the `Lazy` registration is
    deleted. No construction cycle remains because `EngineManagementService` already defers its
    Backup-side dependency (`Lazy<IBackupKeyShredding>`).
- Reviewed every workaround-pattern hit (rule 2). Bounded backoffs/retries against external
  services are real design and stay: S3 multipart part retries (`ObjectStore.cs`), dropped-body
  re-reads (`BackupReader.cs`), ts-auth key refresh + one retry (`UserAuthenticationHandler.cs`,
  `KeyAuthority.cs`, `TsAuthKeys.cs`), file-storage cleanup retry on next tick
  (`FileStorageService.cs`), retention retry timestamps in `ContinuousBackup*`. No patch over a
  bug in our code was found in the slice; `DataKeys.cs:397` race is refused with an explicit
  retryable error by design (custody authority owns the answer).
- Legacy sweep (rule 3): no legacy/compat paths owned by this slice. `LegacyManagementRoute`
  alias handling in `UserAuth/UserAuthExtensions.cs:288-343` is a public HTTP contract
  (legacy route aliases) owned by the Management slice; reported CROSS-SLICE, not changed.
- Tests (rule 4): namespaces in the slice's test folders already match their projects
  (Fast: `TerraBase.Fast.*`, Slow: `TerraBase.Slow.*`). Tests drive `TerraBaseTestServer`
  over HTTP/S3 fakes; `CopyGate` is a test-seam decorator over `IManagementService` (round 1
  recorded why DispatchProxy cannot replace it — CA2012); stays.

## Problems deliberately not fixed

- `ContinuousBackup.cs` (1407 lines): dense but cohesive state machine over `ShippedFile`;
  restructuring in a parallel refactor is too risky (round 1 decision, unchanged).
- `NoBackup` null-object registration: config-driven absence of backup settings, not a feature
  flag; behavior must stay.
- `IManagementService`'s 26 members and the `CopyGate` test decorator: cross-slice (round 1);
  the new narrow interface shrinks this slice's share of the problem.

## THIRD-PARTY (comment deleted, code kept)

- To be filled per file during the comment pass (S3 protocol behaviors, IANA registries,
  ts-auth key semantics, aws-chunked encoding, Tigris anchors).

## CROSS-SLICE (for the coordinator)

- `LegacyManagementRoute` (defined in `Features/Management/ProjectApiRoute.cs`, consumed in
  Hosting/Qos/UserAuth): hidden legacy aliases are a public-contract decision owned by Management.
- 27 of 60 Slow test files outside this slice still declare `TerraBase.Fast.*` namespaces
  (round 1 finding, unchanged).
