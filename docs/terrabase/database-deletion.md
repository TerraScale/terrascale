# Database deletion and restoration

An explicit database DELETE (including the identity-guarded route), dashboard deletion,
MCP deletion and TTL expiry move the database into a deleted state for seven days.
Normal database lookups and new SQL requests cannot reach it. Its immutable identity,
local data, backup metadata and data keys remain available for restoration.

Use a project token without a database restriction:

- `GET /v1/projects/{project}/deleted-databases` requires `databases:read` and
  returns `items` and `next`. Each item has `id`, `projectId`, `name`,
  `deletedAt` and `purgeAt`. Pagination uses `after` and `limit`.
- `POST /v1/projects/{project}/deleted-databases/{deletedDatabase}/restore`
  requires `databases:write` and the immutable database ID. Call the region that
  accepted deletion. HTTP 200 returns the restored database with the same ID and
  data. Restoration clears its former TTL.
- A reused database name or an exhausted database allowance returns 409.
  Restoration at or after `purgeAt` returns 409 `restore_window_expired`;
  after permanent cleanup removes the deleted entry it returns 404.

After the deadline, maintenance commits permanent purge authority before deleting
the retained data and destroying backup keys. Backup retention and orphan-file
cleanup preserve deleted databases during the window. Branches remain independent.

Explicit account-deletion consent and project erasure retain their permanent
erasure behavior, including databases already in the deleted state. They cannot
be undone through these routes. Dashboard restoration controls and SDK convenience
methods are separate follow-up work; these HTTP routes are available directly.
