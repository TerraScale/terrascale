# Turso 0.8.2 native FTS contract

Source: `tursodatabase/turso` tag `v0.8.2`, commit
`5c168be5ef6a32ac966daf5983502a4f694b37ad`. No version upgrade.

```sql
CREATE TABLE t(id INTEGER PRIMARY KEY, title TEXT);
CREATE INDEX t_fts ON t USING fts(title);
SELECT id FROM t WHERE fts_match(title, ?1);
SELECT id, fts_score(title, ?1) AS score FROM t
WHERE fts_match(title, ?1) ORDER BY score DESC LIMIT ?2;
```

Multi-column indexes use `USING fts(title, body)` and pass the same indexed
columns before the query argument to both functions. Higher BM25 scores rank
first. Upstream tests exercise bound query parameters and `ORDER BY score DESC`.
The native smoke also probes `USING fts(json_extract(doc,'$.body'))` and the
matching expression in `fts_match`/`fts_score` in a separate process. The original
amd64 native build observed CREATE INDEX, expression match and positive score
working; expressions are not known to be unsupported. Rejection remains diagnostic
and does not fail an artifact whose derived text-column FTS passed. The engine
uses ordinary derived text columns to materialize bounded candidate scores once.

The native library must compile `turso_sdk_kit` with Cargo feature `fts`
(`turso_core/fts`, backed by Tantivy). `index_method` is a runtime database
capability, not a Cargo feature: pass `index_method` in the C ABI database
configuration's `experimental_features`. The vendored .NET driver's
`TursoBindings.NewOpenedDatabase` supplies this for normal and encrypted opens.
It is always on, with no application setting. Reopened databases require it too.

The build selects `turso_sync_sdk_kit --features turso_sdk_kit/fts`, then renames
its library to `libturso_sdk_kit.so`, exactly as upstream's .NET Makefile does.
This retains the sync ABI used by existing driver methods as well as the core.

Evidence in the pinned upstream tree: `sdk-kit/Cargo.toml`,
`sdk-kit/src/rsapi.rs` (`database_opts`),
`tests/integration/index_method/mod.rs` (`test_fts_sql_queries` and bound-query
tests), and `core/index_method/fts/mod.rs`.

## Delivery and verification

Normal CI, Fast, Slow and image builds download and verify the combined
`turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029-r2` release. They never build Rust:

```sh
nice -n 19 scripts/vendor/download-turso-native.sh
```

The pinned inputs, library hashes, receipts and maintenance/publication process
are in [`src/vendor/Turso.Native/README.md`](../../src/vendor/Turso.Native/README.md).
Maintenance run 40017375521 built twice on each real target architecture;
emulation is rejected. Both independent builds passed the mandatory FTS and
contract1 smokes and produced byte-identical x64/ARM64 library hashes. Compiler
and disassembly receipts verify runtime-dispatched C AEGIS with hardware paths
and portable fallback, without a global hardware-AES requirement. Execution on
hardware without AES was not tested. The exact corrected x64 library passed
2,548 Fast tests, 27 driver tests, and all 27 executable-only single-file driver
tests at source `a5ec485f741e2b5d36413f15dd53d6beaa01417a`. Actual process mappings
and extracted native bytes match the pin. TerraBase publishes without a Turso
sidecar; normal CI retains the executable-only loading regression.

Matched native-only shutdown acceptance passed 18/18 cases per arm in three
alternating pairs. Encrypted median Stop was 1,416.028 ms, 1.0324x matched main
and 1.3353x prior main, within both 1.5x limits. Exact inputs and samples are in
the [measured report](../../benchmark/bench/2026-10-23-runtime-aegis.md).

ELF checks validate the libraries before use and the production apphost before
image publication. Native smoke covers match/score, mutation/rollback, memory
admission, cancellation/recovery and candidate seek plans. The contract smoke
also proves persisted encrypted FTS, marker 1, SQL errors remaining constraints,
and ciphertext authentication damage returning native corruption. ARM64 native
proof does not imply ARM64 managed Fast qualification. Final feature-head CI,
complete benchmark repeats and production live verification are separate gates.
The old security audit describes the replaced NuGet payload; its documented
crossbeam-epoch risk is not claimed resolved by this change.

Batch 5 advances `Engine/Turso/StorageFormat.cs` from main's version 13 to 14.
The deploy lane owns the coordinated reset and production activation.

## Native request budgets

`scripts/vendor/turso-native-budget.patch` is applied deterministically to the pinned
0.8.2 archive before compilation. It adds the internal C ABI
`turso_connection_set_fts_progress_handler(connection, callback, error_out)` and
`SqliteConnection.SetFtsProgressHandler(Func<ulong, ulong, bool>?)`. Configure/clear
only while the connection is idle; callbacks must not reenter Turso. Arguments are
additive work and conservative byte charges; true/nonzero aborts with native
`TURSO_INTERRUPT` (5), mapped to `SQLITE_INTERRUPT` (9) by the driver. The managed
handle roots its delegate until clear/deinitialization; exceptions abort without
crossing the unmanaged boundary.

Start/StopQueryBudget registers/removes this callback for Query/Count/assert_count.
Native charges share the request's existing work/deadline; native bytes use the
plan scan-byte ceiling, capped at 128 MiB even for unlimited plans. Foreground memory
admission is reserved before native allocation and released with the query budget.
Shared segment-byte and assembled-searcher caches retain no payloads: query memory
admission ends after native reader completion/cleanup releases query payloads.
This also prevents invalidated snapshots or earlier writes from leaving resident
segment/searcher references across sequential queries. Turso parks completed cursors
in its connection-owned transaction registry, so FTS resets read state on successful
statement completion as well as transaction commit/close/abort/rollback. This releases
segments, assembled views, scorer streams, result heaps and collection capacities
before per-query admission ends. Published segment IDs remain for write-outcome
bookkeeping until commit/rollback. The explicit-open-transaction regression inspects
parked cursors after distinct-index counts and query-plus-count, then verifies rollback.
Charges include segment residency, temporary chunk copies, identity arrays,
tombstones and scorer/result buffers. They are conservative cumulative charges rather than
measured peak RSS; repeated searches in one request can exhaust the ceiling.

The callback polls loading/assembly, identity extraction, tombstone application,
streaming posting advancement and ordered scoring. Ordered search uses a checked
LIMIT-sized heap instead of opaque TopDocs traversal. Term/document work is charged
before scorer construction. Weight allocation additionally charges 4 KiB per clause;
per-segment scorer allocation charges 64 KiB per clause before constructing weights
or scorers. This covers the pinned Tantivy 0.26.2 per-term 1 KiB BM25 table,
posting decoders/scorer copies and Boolean union score windows, including repeated
physical clauses on tiny indexes. Boolean nodes count separately; hidden boosted subtrees
use bounded source length as a conservative clause count. Phrase-prefix scorers
add the pinned parser's 50 suffix expansions; opaque boosted subtrees reserve that
expansion per positional term. Before any weight/scorer construction, each segment
also reserves 64 bytes per field-wide recorded token per positional term occurrence.
Physical phrases are counted separately through Boolean/boost wrappers. Unmerged
field token metadata includes deleted documents and emitted tokenizer tokens, including
n-grams. After delete compaction, Tantivy reconstructs approximate totals from
downward-quantized fieldnorms (for example, 41 live tokens can be recorded as 40).
The allocation estimates include conservative slack; they are not a strict RSS guarantee.
Three-or-more-term sloppy phrases can duplicate suffix positions during every
intermediate carrying join. Their additional reservation starts with `B = T`, where
`T` is the field's recorded token estimate, then applies `B = B*T + B + T` for each of
the phrase's `terms - 2` intermediate joins and reserves `64*B` bytes. This accounts for
each join's `L*R + L + R` appends, the next join's expanded input, both carrying
position/slop buffers, capacity growth and reallocation. All arithmetic saturates;
an overflowing estimate refuses finite admission before any weight/scorer exists.
The public Tantivy grammar AST supplies the positive-slop flag before the same
AST is converted to a physical query, preserving parser deduplication and errors.
Because physical PhraseQuery hides its slop and BoostQuery hides its tree, a query
containing positive slop conservatively applies the recurrence to every physical
phrase with at least three terms; an opaque boosted subtree uses all its positional
terms per field as the maximum phrase length. Exact queries without positive slop
and ordinary terms retain their earlier charges; no query syntax is banned.
Prefix suffixes reuse one position buffer;
their separate posting decoders are covered by the expansion reservation. The pinned
parser creates ordinary/sloppy phrases and phrase-prefix queries; it does not create
RegexPhraseQuery, and regex syntax is disabled. These are conservative
charges and can reject a long valid query before actual allocations exhaust memory.
**Cancellation is cooperative:** individual Tantivy
parser, weight/scorer/advance and identity-sort calls remain opaque; checks bound
inputs and run between calls, not inside them. This is not a hard wall-clock deadline
guarantee for a single call.

The mandatory C smoke asserts loading-byte refusal, interruption before aggregate
output, ordered traversal interruption, cancellation after the first native reader
row, callback clearing/recovery, insufficient weight/scorer admission for 500 distinct
three-letter terms over one document, and dense sloppy/prefix/boosted physical phrase
admission before scorer construction (including count). Dense clauses use different
slops, prefixes or boosts so deduplication preserves their physical allocations;
identical repeated terms/phrases have separate healthy semantic checks. The 500-clause smoke first measures
setup and the distinct weight/scorer charge boundaries, then refuses each charge
with setup already admitted; it asserts and prints the exact refusal charge and
callback position. A separated-block document (`b ` 8192 times, `a ` 8192 times,
then `c`) and `"a b c"~20000` must refuse carrying scratch at 128 MiB for scored
reader, count and ordered search, while a small admitted three-term slop query works.
It also checks open-transaction distinct-index reads/rollback and an
EXPLAIN QUERY PLAN indexed ID seek for
`(SELECT CAST(value AS TEXT) AS hit_id FROM json_each($ids)) AS hits CROSS JOIN collection ON collection.id = hits.hit_id`.
The explicit TEXT affinity is required for the pinned planner to seek the unique
text ID index; uncast `json_each.value` produces a collection scan. The smoke prints
each actual query/count/sort/cursor plan and rejects a collection scan.
