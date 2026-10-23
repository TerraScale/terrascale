# 16. Home region and live move

- **Placement** lives in the database's catalog document: `home` (LWW region) and `regions` (an add-wins set that
  contains `home`). It is set at create (default: the region that served the create) and is GDPR-relevant: data files
  exist only in `regions`. A machine opens a database's file only when its own region is in `regions`.
- **Routing.** Requests outside a database's placement must reach an authorized serving region.
  The OCI ingress contract requires verification during migration; see [hosting](../../oci.md).
- **`/v1/operations/{id}`** (new, catalog collection `operations`): `{id, kind, database, state:
  pending|running|succeeded|failed, step, progress, error, created_at, updated_at}`. It is written by the machine
  driving the operation and replicated like any document.
- **Move** (`POST …/databases/{d}/move {"region": "sjc"}` → 202 with the operation). Every step is an ordinary lattice
  write:
  1. `add regions sjc`.
  2. The sjc machines open the database and refill (section 10), reporting progress by bucket.
  3. When sjc has every bucket and `applied` within 1 s of every origin, an sjc machine writes `home = sjc`.
  4. It then writes `remove regions gru`.
  5. Gru machines stop serving the database. They ship their remaining CDC until sjc acks all of
     it, then delete the file.
  6. The operation ends `succeeded`.

  Writes accepted in gru at any point before step 5 are local CDC that gru ships before deleting, so none are lost.
  Backups ship from `home` (Tigris, later).
