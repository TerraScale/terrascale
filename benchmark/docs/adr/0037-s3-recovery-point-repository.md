---
status: accepted
date: 2026-08-30
authority: direct workspace-owner instruction
---

# S3 recovery-point repository

## Context

The first TerraBase backup implementation wrote full and incremental archive
chains to a local directory. It also carried a private timer, local retention,
custom compression, and custom bundle encryption. Studio exposed only full
local backups. Keeping a second block volume large enough for those archives
costs more than object storage and leaves restore coupled to one machine.

Litestream has the desired operating model but only understands SQLite pages
and SQLite WAL files. TerraBase uses its own WAL and LSM formats. Running
Litestream against a TerraBase location would not produce a restorable backup.

## Decision

S3-compatible object storage is the durable backup destination. TerraBase
keeps its logical archive records, content-derived recovery-point identity,
SHA-256 member bindings, and restore verifier. Those are storage-format code,
not destination infrastructure.

The transport follows these rules:

- It writes archive members under a prefix dedicated to one database, then
  writes `manifest.tbv2m` last as the recovery-point commit marker.
- Object keys are immutable. An identical retry reuses identical bytes. A
  retry that finds different bytes under the same key fails.
- Normal runs append incremental points. A configured interval creates a new
  self-contained full point.
- Uploads and downloads have fixed concurrency. Each archive member remains
  bounded by the engine's member limit.
- Restore lists and validates manifests first, selects one chain, downloads
  only that chain into a process-unique scratch directory, and uses the
  engine's existing checksum and clean-target restore path.
- Transfer scratch is disposable. S3 is the only durable backup copy.
- Bucket encryption policy is the default. An optional KMS key adds SSE-KMS
  headers. Access credentials come from workload identity, task or instance
  roles, or standard AWS environment providers. Studio never persists them.
- S3 lifecycle policy owns retention. Bucket versioning and object lock are
  deployment controls. TerraBase does not issue broad prefix deletion from a
  backup run.

The old private local scheduler, local destination configuration, bundle
codec, and local retention implementation are removed. Studio owns the first
in-process S3 scheduler and setup UI. A production engine host must compose the
same adapter in the process that owns the live `Engine`; a second process must
not open the active data directory to imitate a sidecar.

## Consequences

Backups no longer require a persistent backup block volume. S3-compatible
providers, including AWS S3 and compatible private endpoints, use the same
object layout and restore path.

The archive remains a TerraBase format. It is not a raw filesystem copy and
cannot be restored by Litestream. This preserves tenant selection, delete
records, durability disclosures, scope binding, and clean replay verification.

Deleting one object can break an incremental chain. Lifecycle rules must
expire a full point and all incrementals that depend on it as one retention
unit. The operator runbook gives the safe policy.

Studio is loopback-only and its scheduler stops with Studio. Its successful
local S3 test is implementation evidence, not production backup or restore
qualification.

## Required evidence

- In-memory object-store tests prove immutable, idempotent publication.
- Engine tests prove canonical manifest decoding and local archive restore.
- An S3-compatible integration lane proves manifest-last upload, restart,
  incremental chaining, selected-chain download, corruption refusal, and
  clean restore.
- Production qualification restores a candidate-bound database into a new
  cluster and records RPO, RTO, object count, bytes, provider, Region, KMS key
  identity, lifecycle policy, and checksum verdict without credentials.

