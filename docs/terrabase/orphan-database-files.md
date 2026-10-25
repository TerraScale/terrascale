The management file audit runs at startup and every fifteen minutes. It reports
unregistered file counts and bytes through `terrabase.storage.unknown_files` and
`terrabase.storage.unknown_bytes`, and logs each tenant file's size and first-seen
time. Observations live in the system catalog, scoped to the serving node.

Recognized tenant files become eligible after 24 hours of observed absence from
the catalog. Registered databases, tracked backup snapshots, cleanup intents,
purges, publication, initialization, rebuild and salvage work are preserved.
PITR staging has a cleanup intent until its restore completes. Unknown paths,
links, directories, SYSTEM and the intents journal are never sweep targets.

Deletion rechecks the current catalog under its writer and the engine lifecycle
lock, closes cached stores, and defers unlink while native handles remain. An
unsupported catalog or closed catalog gate never authorizes deletion. First-seen
records reset when files disappear or become owned. No API or storage format changes.
