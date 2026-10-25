# Unregistered database file audit

Every 15 minutes, after catalog verification, TerraBase records node-scoped first-seen times in its system catalog. This pass only observes: it never deletes database files. Project database rows and tracked backup records share the backup discovery registration rule; cleanup intents, purges and engine lifecycle operations also retain ownership.

Each orphan database produces one warning per pass with its summed sidecar bytes and first-seen time. `terrabase.storage.unknown_files` remains a physical-file count, now excluding intent-owned and purge-owned files; `terrabase.storage.unknown_bytes` measures their bytes. Labels contain no tenant identifiers. Unrecognized paths remain preserved and counted. Unsupported catalogs preserve all files without interpreting tenant rows or changing first-seen records.

During catalog outages or lifecycle-lock contention, the last complete gauge values and first-seen records remain available until a complete pass succeeds.
