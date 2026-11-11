# Native full-text qualification

The pinned SQL runtime supplies indexed full-text match and scoring primitives. This is an internal runtime qualification contract; native capability alone does not establish a public SQL API. Customer-visible SQL support remains governed by the SQL sandbox and served API reference.

## SQL primitives

```sql
CREATE TABLE t(id INTEGER PRIMARY KEY, title TEXT);
CREATE INDEX t_fts ON t USING fts(title);
SELECT id FROM t WHERE fts_match(title, ?1);
SELECT id, fts_score(title, ?1) AS score
FROM t WHERE fts_match(title, ?1)
ORDER BY score DESC LIMIT ?2;
```

Multi-column indexes use `USING fts(title, body)` and pass the same indexed columns before the query argument to both functions. Higher BM25 scores rank first. Qualification must exercise bound query parameters, scoring order and reopening the index. An expression-index probe is separate from acceptance of the required derived-text-column path.

## Build and loading

The vendored native build in [the vendor source](../../../src/vendor/) pins its upstream source, verifies archive and library hashes, and produces the combined core/sync runtime for both supported Linux architectures. Full-text support is compiled in and initialized on every ordinary and encrypted database open, including reopen. There is no application setting.

Normal application builds consume verified artifacts rather than rebuilding the native runtime. Maintenance builds run through the approved dev-host runner. Native ABI, packaging, executable-only loading and the combined sync exports must remain qualified together; passing a route test does not establish native loading parity.

## Required verification

- Indexed match and positive score, including bound queries and multi-column arguments.
- Insert, update, delete, rollback and reopen behavior, with encrypted persistence.
- Memory/work admission, cancellation, cleanup and successful use after interruption.
- Candidate seek plans and bounded score materialization.
- Constraint failures retain their typed error mapping; ciphertext authentication damage remains a corruption refusal.
- Independent builds on each real target architecture produce matching artifact hashes and verification receipts.

Native progress callbacks are installed and cleared only while the connection is idle. They must not reenter the engine. Their managed delegate remains rooted until cleared or deinitialized; exceptions abort without crossing the unmanaged boundary.

Architecture-specific native smoke results do not imply qualification on another architecture, hardware without the tested instruction set, or successful production rollout. Preserve the exact-source build receipts and separate runtime and live proofs. See [native SQL](native-sql.md), [resource admission](../qos.md) and CI.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
