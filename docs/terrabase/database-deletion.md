# Database deletion and restoration

An explicit database DELETE (including the identity-guarded route), dashboard deletion,
MCP deletion and TTL expiry move the database into a deleted state for at least seven days.
Normal database lookups and new SQL requests cannot reach it. Its immutable identity,
local data, backup metadata and data keys remain available for restoration. Functions
and their schedules cannot run while the database is deleted; schedules resume after
restoration. Retained databases still count against the organization's storage quota.
Deleting a database releases its live database slot, but releases storage only at purge.

Use a project token without a database restriction:

- `GET /v1/projects/{project}/deleted-databases` requires `databases:read` and
  returns `items` and `next`. Each item has `id`, `projectId`, `name`,
  `deletedAt` and `purgeAt`. Pagination uses `after` and `limit`.
- `POST /v1/projects/{project}/deleted-databases/{deletedDatabase}/restore`
  requires `databases:write` and the immutable database ID. Call the region that
  accepted deletion. HTTP 200 returns the restored database with the same ID and
  data. Restoration clears its former TTL.
- A reused database name, exhausted database allowance, exceeded organization storage
  quota or existing permanent purge returns 409. Restore counts the retained data
  exactly once when checking storage.
  Restoration remains available until permanent purge is authorized; after that it returns 404.
- `DELETE /v1/projects/{project}/deleted-databases/{deletedDatabase}` requires
  `databases:write`, the same scope as restore, and a project token without a database
  restriction. It durably authorizes permanent destruction, ending the restore window.
  HTTP 202 returns `{ id, state: "destroying", outstanding }` while verification is
  pending. Outstanding kinds are `files`, `keys`, and `backups`; no host identities
  are exposed. HTTP 204 confirms verified destruction everywhere. Repeating a pending
  request returns the same database-id job's current state; a later DELETE after
  completion returns 404 gone. This action cannot be undone.
- `GET /v1/projects/{project}/deleted-databases/{deletedDatabase}` requires the
  same project authorization and `databases:write`; it returns the purge job,
  including `state: "complete"` with an empty outstanding list after completion.

An ordinary database DELETE answers 204 and begins retention; it destroys no data or keys.
The database DELETE response includes `TerraBase-Storage-Released: at-purge`.
A call outside the database's placement returns 421 `wrong_region`. A move in
progress returns 409 `move_in_progress`; deletion, restore or purge outside the home
region returns 409 `wrong_region`. Other regions drop their copies when deletion
is accepted, and are re-seeded after restore.

Every retention transition requires the current regional writer's lease and epoch,
checked again inside the catalog commit. An outgoing writer's retained copies carry
no authority after handoff. Authenticated permanent purge supersedes retention on
every holder, so retained records cannot protect files from authorized destruction.

A regional writer handoff transfers retained home databases outside the catalog
snapshot's unchanged 10,000-row and 4 MiB aggregate bounds. The retained protocol
allows up to 1,000,000 databases, in pages of at most 100 records and 256 KiB of
stored payload. Project-index records are reconstructed. The receiver verifies a
manifest count and SHA-256 digest over ordered database keys, and each certified
database position, before promotion. A 10,000-database transfer requires at least
100 pages; elapsed time depends on file sizes and catch-up I/O, and has not yet
been measured. The existing 30-second drain budget still fails closed if exhausted.
The incoming writer imports their files before completing the handoff. Retained page
pulls require the internal listener, a valid peer credential and a known peer in the
same region. Clients and other regions cannot pull a retained database.

Restore returns 503 with Retry-After while retained files are unavailable. Purge also
refuses missing retained files: it preserves the retained record, keys and backups,
increments terrabase.purge.errors with reason retained_files_missing and logs the
database ID. Maintenance can purge after the files return.

Scheduled expiry waits until the writer and at least one other node independently
confirm that seven days plus a one-hour margin have passed since deletion. It asks
the in-region partner first, then another region if the partner does not answer.
With no confirmation, retention continues and
`terrabase.retention.expiries_waiting_confirmation` reports waiting expiries.
Explicit permanent purge is independent of clocks. The clock-trust change will
add its own guard.

Once confirmed, maintenance commits permanent purge authority before deleting
the retained data and destroying backup keys. Backup retention and orphan-file
cleanup preserve deleted databases during the window. Branches remain independent.

Explicit account-deletion consent and project deletion retain their permanent
erasure behavior, including databases already in the deleted state. A project with
only retained databases can be deleted immediately: this explicit action permanently
erases them in every region. Live databases and other live project resources must
still be removed before the console accepts project deletion. Neither action can
be undone through these routes. Dashboard restoration controls and SDK convenience
methods are separate follow-up work; these HTTP routes are available directly.


Incoming replicated writes for a retained database currently retry because its live
registration is missing. They must remain durable until restoration permits applying
them or permanent purge permits discarding them. A consumer that parks writes must
use only `DatabasePurge.IsPermanentlyPurgedAsync` to acknowledge without applying;
retained-record presence must never trigger that acknowledgement.

Release order, with every node upgraded before the next step: handoff-import
compatibility, retained-deletion receivers, then retention-producing deletion.
