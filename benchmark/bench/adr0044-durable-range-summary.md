# Durable range-summary operation bounds

These are deterministic storage operation counts, not measured latency or
throughput. Run `cargo test -p terrabase repair_summary` at lowered CPU priority
for correctness evidence. End-to-end throughput qualification remains separate.

| Work | Before this implementation | Durable implementation |
| --- | --- | --- |
| Source fingerprint state | Driver holds every source publication hash | SQLite immutable 32-byte identity per tenant/origin/sequence |
| Summary maintenance per new source publication | In-memory aggregate update | 10 version inserts, one per 4096-row leaf / 64-way ancestor, plus one origin version and identity |
| Complete aligned range | Reconstructed from driver's source cache | Indexed latest version at or below frozen local cut |
| Arbitrary range | Source cache traversal | At most two partial leaves (8190 identities), plus at most 126 aggregate lookups per hierarchy level |
| Differing leaf delivery | Cached source identity lookup | At most 4096 source positions; full canonical fingerprints revalidated; 64 MiB output bound |
| Duplicate source identity | Driver dependent | Matching hash adds no version; conflicting hash rejects transaction |
| Historic versions | None durable | Thirty-day horizon and minimum consumer frontier; one baseline retained per bucket |

The same SQLite source-publication transaction writes identities and summaries;
rollback removes all of them. Full identities survive source-outbox retention:
requesting unavailable source bodies fails closed instead of silently proving
an incomplete set. Summaries bind the database incarnation through the engine
cut and tenant through every primary key. Cuts expire after five minutes and
cannot read below the persisted compaction floor. The history sweep retains the
maximum declared history horizon (30 days); consumers can pin older versions.

Existing populated files without source-summary metadata are rejected at open;
there is no startup database rescan or inferred empty summary. Source outbox
retention is separate from compacting aggregate versions. Original bodies and
immutable identities still require the broader replay/bootstrap retention
policy; this change does not claim bounded total history storage.
