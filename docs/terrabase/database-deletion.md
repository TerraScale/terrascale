# Database deletion and restoration

An explicit database DELETE (including the identity-guarded route), dashboard deletion,
MCP deletion and TTL expiry move the database into a deleted state for seven days.
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
  Restoration at or after `purgeAt` returns 409 `restore_window_expired`;
  after permanent cleanup removes the deleted entry it returns 404.
- `DELETE /v1/projects/{project}/deleted-databases/{deletedDatabase}` requires
  `databases:write`, the same scope as restore, and a project token without a database
  restriction. It permanently purges the database immediately, ending the restore
  window and releasing its storage. HTTP 204 confirms purge; this action cannot
  be undone. Call the region that accepted deletion.

The database DELETE response includes `TerraBase-Storage-Released: at-purge`.
A call outside the database's placement returns 421 `wrong_region`. A move in
progress returns 409 `move_in_progress`; deletion, restore or purge outside the home
region returns 409 `wrong_region`. Other regions drop their copies when deletion
is accepted, and are re-seeded after restore.

A regional writer handoff includes retained home databases in its certified positions.
The incoming writer imports their files before completing the handoff. Retained page
pulls require the internal listener, a valid peer credential and a known peer in the
same region. Clients and other regions cannot pull a retained database.

Restore returns 503 with Retry-After while retained files are unavailable. Purge also
refuses missing retained files: it preserves the retained record, keys and backups,
increments terrabase.purge.errors with reason retained_files_missing and logs the
database ID. Maintenance can purge after the files return.

After the deadline, maintenance commits permanent purge authority before deleting
the retained data and destroying backup keys. Backup retention and orphan-file
cleanup preserve deleted databases during the window. Branches remain independent.

Explicit account-deletion consent and project deletion retain their permanent
erasure behavior, including databases already in the deleted state. A project with
only retained databases can be deleted immediately: this explicit action permanently
erases them in every region. Live databases and other live project resources must
still be removed before the console accepts project deletion. Neither action can
be undone through these routes. Dashboard restoration controls and SDK convenience
methods are separate follow-up work; these HTTP routes are available directly.
