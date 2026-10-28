# Retention and daily restore drills

PR #772's rebased CI run 40710562957 reproduced #742: the daily drill selected an older
snapshot while the same shipping pass deleted that snapshot through retention. The
drill reported `a backup object of this database disappeared during the restore`.

Retention now runs after pending WAL ships and before starting a new snapshot upload
and drill. It first joins the file's previous side work and collects its upload result.
Other databases keep their independent shipping jobs. Retention intervals, plan windows,
expiry cutoffs and failure reporting are unchanged. The active generation's content-free
lineage certificate remains available when all its old data expires, because its next
snapshot still uses that generation; inactive, unreferenced certificates still expire.

The unchanged retention/drill scenario returns from Slow to Fast. A new Fast regression
holds a real drill's bucket download across a clock advance and another shipping pass.
It checks that retention waits without deleting the input, another database can upload,
and the expired input disappears after the drill finishes successfully. Resources and
barriers belong to each test; parallel execution stays enabled. CI qualifies the change;
no local .NET run is claimed.

No public API, storage format, migration or authentication behavior changes.
