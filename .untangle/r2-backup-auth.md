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

All line numbers are post-deletion anchors in the surviving code.

- `Backup/ObjectStore.cs:139` signed payload hash instead of aws-chunked encoding — S3-compatible services do not all accept aws-chunked.
- `Backup/ObjectStore.cs:47-48` 128 MiB × 10,000 part budget — S3's 5 GiB single-PUT cap and 10,000-part multipart limit.
- `Backup/ObjectStore.cs:204` multipart ETag is S3's MD5-of-part-MD5s protocol; opaque ETags on some stores.
- `Backup/ObjectStore.cs:202-212` S3-compatible services may answer a failed multipart completion with 200 plus an error body; completion is proven by length + upload-id metadata or the multipart ETag.
- `Backup/ObjectStore.cs` (request pipeline) one permanently-subscribed response reader — the AWS SDK's client reads its response event twice per response, so per-request subscribe/remove raced (NullReferenceException in ProcessResponseHandlers).
- `Backup/ObjectStore.cs` (deadline policy) each attempt is one request under RequestTimeout including the SDK's own internal retries.
- `Backup/ObjectStore.cs:457` mutable security anchors must observe the latest write across Tigris regions (X-Tigris-Enable-Snapshot).
- `Backup/ObjectStore.Purge.cs` version-list implementations that omit current objects (Tigris) must not be read as certifying absence.
- `Backup/ObjectStore.cs` (listing) sixfold XML expansion of 1024-byte keys bounded within the customer 2 MiB response limit.
- `Backup/ObjectStore.cs:471` S3 orders UTF-8 bytes; Unicode scalar comparison matches it without encoding.
- `Backup/CustomerBackupNetwork.cs` IANA IPv4/IPv6 special-purpose registries define the blocked address ranges.
- `tests/TerraBase.Slow/Backup/DatabaseBackupPurgeTests.cs` Tigris lists unversioned buckets as version "null" and truncates with only NextKeyMarker.
- `UserAuth/TsAuthKeys.cs:141` (area) ts-auth JWKS: only asymmetric keys accepted; a symmetric "oct" key would let any holder sign tokens.
- `UserAuth/UserAuthenticationHandler.cs:70` (area) ts-auth may rotate in a key after the last fetch; one rate-limited refresh, then one more attempt.
- `UserAuth/KeyAuthority.cs:218` (area) an answer's refusal always holds; refreshes are background once aged (ts-auth key-status contract).
- `Backup/BackupReader.cs:381` (area) the store may drop an object body mid-stream; reads restart from the beginning, bounded to three attempts.
- `Backup/ObjectStore.cs` (customer deadline) customer endpoints are untrusted; one wall-clock deadline also bounds streamed bodies the SDK's header timeout does not.


## Round 2 continuation (coordinator review)

- `FileStorageService.cs` sweep deadline handled where it is raised: the 20-second
  per-sweep budget now ends the pass inside `SweepAsync` with an explicit return for its
  own cancellation; the tick loop's empty `catch (OperationCanceledException) { }` is gone
  and shutdown cancellation propagates.
- `UserAuthExtensions.cs` (459 lines) split: DI wiring, claim helpers and one-line
  middleware registrations stay; the bearer challenge moved to `BearerChallenge.cs`, the
  key-watch cutoff and claim gate to `ProjectCheck.cs`, the project-catalog middleware to
  `ProjectCatalog.cs`, socket admission to `SocketAdmission.cs`, project granting to
  `ProjectGrants.cs`. Pure move, no behavior change.
- `ContinuousBackup.cs` (1384 lines) split by concern: WAL segment shipping, sealing and
  failure handling moved to `ContinuousBackup.Shipping.cs`; generations, daily snapshots
  and side work to `ContinuousBackup.Snapshots.cs`; lifecycle/scheduling stays in the core
  file with Lineage and Maintenance. Pure move of method groups, no body touched.
- `BackupReader.BuildAsync` six-argument forwarding overload (one caller) removed; the
  caller passes `next: null` directly.
- Listing and paging reviewed across `ObjectStore`, `PointInTimeRestore`, `BackupReader`,
  `DatabaseBackupPurge`, `DataKeys`: listing is centralized in `IObjectStore.ListAsync`
  and key parsing in `BackupLayout`; the consumer loops deliberately differ in scan-budget
  semantics (raw-item counting vs parsed-only) that are load-bearing, so fusing them would
  need policy flags and was not done. Key wrap/unwrap lives only in `MasterKey`.
- Test-only seams: none found; `PrepareScratch` and `RunOnceAsync` are used by production
  paths and the shared integration-test harness.

## CROSS-SLICE (for the coordinator)

- `LegacyManagementRoute` (defined in `Features/Management/ProjectApiRoute.cs`, consumed in
  Hosting/Qos/UserAuth): hidden legacy aliases are a public-contract decision owned by Management.
- 27 of 60 Slow test files outside this slice still declare `TerraBase.Fast.*` namespaces
  (round 1 finding, unchanged).
