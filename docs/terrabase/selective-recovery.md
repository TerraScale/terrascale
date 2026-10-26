# Selective historical recovery (TS-1006)

Selective recovery restores approved historical **document contents** into the original database. It does not create a customer database or consume another database slot. The temporary restored file stays in isolated service scratch, outside tenant database discovery and backup shipping, and closes and is removed on completion, failure or cancellation. Restored document writes still consume the ordinary storage and operation allowances; TS-980 and TS-995 admission remain unchanged.

Both routes require ordinary project authorization with `databases:write` and current writable access to the source. Preview additionally requires `documents:read`; apply requires `documents:write`. `{database}` is its name. Choose a retained timestamp from `GET /v1/projects/{project}/databases/{database}/point-in-time`. The PITR completeness, retention and backup authentication rules also apply here.

```http
POST /v1/projects/{project}/databases/{database}/point-in-time/recovery-preview
Content-Type: application/json

{"pointInTime":"2026-10-25T12:00:00Z","selections":[{"collection":"orders","ids":["01K7YX83Q7BQTPQ1G4NZ2V8DKA"]}]}
```

`destination` optionally selects `platform` (default) or `customer`. Supply 1–16 distinct collection selections. Omit `ids` to preview all historical live records of a collection. Explicit ids must be distinct canonical ULIDs and exist as historical live records. Each preview is limited to 100 records and 1 MiB of combined historical/current JSON. Larger collections require explicit id batches; exceeding a bound returns 400 without approving a partial selection. Request bodies are limited to 128 KiB. Recovery scratch admits at most two operations per node and one per source, refusing excess work with 503 `capacity` and `Retry-After`. Scratch holds the shared node disk reservation until its native reader closes and the files are deleted; insufficient headroom returns 503 `disk_capacity` with `Retry-After`. Memory admission may also return 503; reconstruction has a two-minute deadline. Preview historical and current JSON reads are metered normally.

The response contains actual historical and current JSON, an exact validator of each current state, and an opaque approval:

```json
{"approval":"<opaque>","expiresAt":"2026-10-25T12:10:00Z","records":[{"collection":"orders","id":"01K7YX83Q7BQTPQ1G4NZ2V8DKA","historical":{"total":42},"current":null,"currentVersion":"tbv1-<validator>"}]}
```

`current` is null when absent/deleted; `currentVersion` still identifies a retained tombstone and is null only for never-written state. Compare the returned contents before choosing records. Historical live records only are candidates: recovery never deletes a current record merely because it was absent historically. Collections must exist both historically and currently. Explicitly recreate a dropped source collection through the ordinary collection API, then preview again. Historical ACLs, schemas, indexes and TTL settings are never restored; current schema and permissions continue to apply.

```http
POST /v1/projects/{project}/databases/{database}/point-in-time/recover
Content-Type: application/json

{"approval":"<opaque>","records":[{"collection":"orders","id":"01K7YX83Q7BQTPQ1G4NZ2V8DKA"}]}
```

`records` must be a nonempty distinct subset of the preview's keys. Neither historical JSON nor expected versions can be supplied. Approval is authenticated by the issuing server, expires after ten minutes, and binds the actor, original database, timestamp, destination, historical content hashes and current validators. Pin apply to the same server process; another server or a restart requires a fresh preview (`409 recovery_preview_invalid`). Approval is held by the client; the server persists no preview application data.

Apply reconstructs retained history again and verifies approved content hashes, rechecks writable source access, and submits the approved puts together through the ordinary document transaction service. Against this region's applied state, like ordinary `If-Match`, any changed current validator—including a newer deletion or a record created after a never-written preview—returns 412 and rolls back the entire batch. This does not provide a global precondition over writes not yet replicated to this region. Current storage quota exhaustion returns 429; schema, uniqueness and ordinary write failures retain their usual codes. No unapproved record is written. A successful response is the existing transaction response (`offset`, `durability`, `results`), with results in the requested record order. `TB-Durability` has its ordinary document-write meaning; do not automatically retry an ambiguous `write_outcome_unknown`.

Source deletion/purge and current authorization fences still apply; an approval grants no access by itself. All platform databases unconditionally refuse selective recovery with `409 recovery_erasure_fenced`, preserving platform history-erasure requirements even before an erasure receipt exists. Whole-database PITR and its database-count admission are unchanged. This adds API contracts only and changes no persisted storage format.
