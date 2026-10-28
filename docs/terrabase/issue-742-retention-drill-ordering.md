# Retention and daily restore drills

PR #772's rebased CI run 40710562957 reproduced #742: the daily drill selected an older
snapshot while the same shipping pass deleted that snapshot through retention. The
drill reported `a backup object of this database disappeared during the restore`.

Retention runs after pending WAL ships and before starting a new snapshot upload and
drill. A per-file handoff prevents a drill from selecting inputs while retention runs.
Ordinary shipping and retention inventories do not wait for an existing drill. If actual
object deletion is necessary, retention cancels and joins that drill before deleting
its possible inputs. The next snapshot performs the daily check against retained history.
Cancellation does not publish a passed drill. Genuine drill failures remain visible.

Other files retain independent shipping jobs. Retention intervals, plan windows and
expiry cutoffs are unchanged. The active generation's content-free lineage certificate
remains available when all its old data expires, because its next snapshot still uses
that generation; inactive, unreferenced certificates still expire.

The original retention/drill scenario returns from Slow to Fast with unchanged assertions.
A new Fast regression holds a real drill's bucket download across a clock advance and
another shipping pass. It checks that expiry cancels and joins the reader before deletion,
another database can upload, the expired input disappears, and the replacement drill
passes. Resources and barriers belong to each test; parallel execution stays enabled.
Existing tests also require WAL shipping and customer isolation while drills are held.

No public API, storage format, migration or authentication behavior changes. CI remains
the exact-head merge gate.
