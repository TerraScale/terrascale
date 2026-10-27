# Unregistered database file audit

Every 15 minutes, after catalog verification, TerraBase records node-scoped first-seen times in its system catalog. Project database rows and tracked backup records share the backup discovery registration rule; cleanup intents, purges and engine lifecycle operations also retain ownership.

Each orphan database produces one warning per pass with its summed sidecar bytes and first-seen time. `terrabase.storage.unknown_files` remains a physical-file count, now excluding intent-owned and purge-owned files; `terrabase.storage.unknown_bytes` measures their bytes. Labels contain no tenant identifiers. Unrecognized paths remain preserved and counted. Unsupported catalogs preserve all files without interpreting tenant rows or changing first-seen records.

## Deletion

An orphan first seen at least 24 hours ago is deleted by the next complete pass, at most 16 per pass. Under the engine's lifecycle reservation, one catalog transaction re-checks that the database is still unregistered, without cleanup intent, purge or deletion authority, and that its first-seen record is unchanged; it then records a cleanup intent and drops the record. The files go through the abandoned-creation cleanup: the engine closes the cached store and deletes the main file, sidecars and artifacts, then the intent clears. A registration that arrives before the cleanup keeps the files. A busy engine operation, an unreadable catalog or a failed cleanup keeps the files, and the intent retries through recovery.

During catalog outages or lifecycle-lock contention, the last complete gauge values and first-seen records remain available until a complete pass succeeds, and nothing is deleted.
