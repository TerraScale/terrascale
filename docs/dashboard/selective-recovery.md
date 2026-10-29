# Selective record recovery

Open a database's **Backups → Recover selected records**. Choose a retained
UTC timestamp inside the continuous PITR window and a current collection. The
window comes from `GET …/point-in-time` with `databases:read`, independently of
manual/scheduled whole-database backups. The input defaults to the latest point;
browser min/max and server validation use the current window. Optionally enter explicit ULIDs, one per line;
empty IDs select historical live records in that collection. Preview is bounded
to 100 records and 1 MiB of combined historical/current values. Larger selections
need explicit ID batches.

Compare current and retained JSON, then check each record to approve. Restore
writes only that subset into the original database. The service checks the
approval and current versions, including tombstone versions. A conflict refuses
the complete apply. Preconditions use the accepting region's applied state, like
If-Match; unseen concurrent regional writes follow normal conflict rules.
Re-preview after a conflict, expiry, restart, or node change;
approval expires after ten minutes. No approval is preselected or persisted by
the dashboard. Recovery responses are not cached.

The service retains authority over access, document quotas, schemas, retention,
and erasure. A dropped collection must be recreated explicitly before preview;
historical schemas and permissions are not restored. No database is provisioned
and no quota is raised. Existing full-snapshot restore remains a separate flow.

The dashboard calls the project-scoped `point-in-time/recovery-preview` and
`point-in-time/recover` endpoints with current-user `databases:write` plus
`documents:read` for preview or `documents:write` for apply and the existing
home-region pin. Platform sources are read-only. Capacity failures show the
service's Retry-After delay. Apply carries only the opaque approval and
explicit collection/record identities; JSON and version claims stay server-side.
