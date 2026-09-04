# ADR 0045: The auto-indexing engine, on a research base

Status: accepted (owner decision D-31, 2026-09-03).
Authority: the workspace `GOAL.md` owns D-31; this ADR is the detailed design
and names the papers and production systems each mechanism is built on.
Supersedes the D-31 section of ADR 0040; the parse-once byte pipeline and
"extraction in Rust, never SQLite expression indexes" rules of ADR 0040 stand.

## 0. The claim we are making

"RavenDB-class or better" means: a developer never writes `CREATE INDEX`,
the first uncovered query is answered, indexes appear and disappear as the
workload changes, and **no index ever makes a query slower or serves a stale
read**. The last clause is what the research base is for: every published
production auto-indexer (Azure SQL, Oracle) learned that recommendation
quality is not the hard part; *verification and revert* are.

## 1. Research base

| Mechanism | Paper / system | What we take from it |
| --- | --- | --- |
| Lifecycle and verification | *Automatically Indexing Millions of Databases in Microsoft Azure SQL Database*, Das et al., SIGMOD 2019 | Recommender + validator + control plane; validate on **logical** metrics (pages read, rows examined), only on statements whose plan actually changed, with a Welch t-test over executions before and after; auto-revert on regression; every action visible in a history. |
| Invisible build, execution verification, plan pins | *Automatic Indexing in Oracle*, Chakkappen et al., VLDB 2025 | Build candidates invisible to the planner; verify by executing captured statements with a time cap of (past average + threshold); make visible only if improved; pin regressed statements to their old plan instead of dropping the index; blocklist (statement, candidate) pairs that failed; expire unused builds. |
| Selection without a trusted what-if optimizer | *DBA Bandits* (Perera et al., ICDE 2021) and *No DBA? No Regret!* (TKDE 2023) | Online index selection as a contextual combinatorial bandit (C²UCB): arms are candidate indexes, reward is measured work saved, a greedy oracle picks the super-arm under budget, and the regret bound is the safety argument. Learns from execution, not from an optimizer's guess. |
| Exploration discipline | *UTune*, 2026 | Weight candidate benefit by model uncertainty so under-observed candidates get bounded, deliberate exploration and stable workloads converge fast; exploration builds are the expensive part, so ration them. |
| Composite construction | *Extend*, Schlosser et al., ICDE 2019; Kossmann et al., VLDB 2020 (evaluation) | Grow composites by recursively adding the attribute with the best benefit-per-byte; simple heuristics with good candidate sets are within a few percent of optimal and orders of magnitude cheaper than exact methods. |
| Workload compression | ISUM (Siddiqui et al., SIGMOD 2022); AutoIndexer, 2025 | Tune templates, not statements: normalize parameters, weight by frequency and cost, keep the column set that covers most benefit; the candidate space stays small enough for a bandit per collection. |
| Hypothetical indexes in SQLite | `sqlite3_expert` (SQLite extension) | The planner can be asked which of a set of hypothetical indexes it would use, using `sqlite_stat1`-shaped statistics in a scratch schema. This is our compilation check. |
| Serving during build | Database cracking / adaptive merging, Idreos et al., CIDR 2007, SIGMOD 2011 | An index that is partially built is still useful. SQLite B-trees cannot be cracked, so we take the principle via a backfill watermark and hybrid plans. |

Not adopted, and why: deep RL advisors (SWIRL, AutoIndexer, Indexer++) need
per-database training and offer no guarantee; LLM advisors (LLMIdxAdvis,
AMAZe, 2025) are slow, non-deterministic, and cannot run per tenant per
minute; learned benefit estimators (RIB, SIGMOD 2026) are trained on a large
engine's plan encodings, our planner is SQLite's and our tenants are small,
so a linear bandit model with hand features is the right size (RIB stays an
option to replace that model later); asynchronous indexes (RavenDB) violate
zero stale reads; SQLite expression indexes re-parse bodies per write.

## 2. Vocabulary

- **Descriptor**: which JSON field (or path) is extracted into a STORED column
  at write time, with its type. Descriptors are database-level schema state,
  journaled and replicated; every Region extracts the same columns.
- **Index**: a SQLite index over one or more extracted columns (or the FTS5
  table for `MATCH`). Physical, per Region; its *lifecycle state* is
  journaled so Regions agree on what is visible to the planner.
- **Template**: a SQL statement with literals replaced by parameters
  (D-54/D-78 plans are already normalized). The unit of workload.
- **Logical cost** of an execution: pages read + rows examined (from
  `sqlite3_stmt_status`), never wall time (Azure's lesson: logical metrics
  have low variance and track plan quality).

## 3. The loop

```
observe ──▶ compress ──▶ candidates ──▶ compile-check ──▶ select (bandit)
   ▲                                                          │
   │                                                          ▼
 monitor ◀── active ◀── verify (shadow) ◀── build (invisible, budgeted)
   │             │
   ▼             ▼
 revert       retire / merge
```

### 3.1 Observe

Every execution records `(template, collection, logical cost, rows returned,
whether an index was used, which)` into an in-memory ring per database,
flushed with the usage-fact wave (D-42). Per extracted column the write path
maintains a distinct-count sketch (HyperLogLog) and a small equi-depth
histogram, updated from the values it already has in hand; these replace
`ANALYZE`, which we never run on the hot path.

### 3.2 Compress

Templates are weighted by `frequency × logical cost` over a sliding window
(default 24 h, decayed). Per collection the top templates covering 95 % of
weight are the tuned workload; the rest are ignored until they climb. This is
ISUM-style compression at template granularity, which is enough because SDK
builders and access rules generate a small template set per app.

### 3.3 Candidates

For each tuned template: the classic column order (equality predicates,
then the single range predicate, then `ORDER BY` keys, then projected
columns if the covering variant is cheap), grown with Extend's rule (add the
attribute with best estimated benefit per index byte, stop when the gain is
below 5 %). JSON-path predicates on non-extracted fields propose a descriptor
plus an index. FTS5 `MATCH` proposes the FTS kind. Access rules (D-39) are
mandatory candidates that skip the bandit. Candidate set per collection is
capped (default 32), oldest-unused evicted.

### 3.4 Compile check

Candidates are materialized as hypothetical indexes in a scratch schema with
statistics from the sketches (`sqlite3_expert` technique) and the template
is planned; a candidate the planner would not pick is dropped immediately.
This removes most of the exploration cost the bandit would otherwise pay.

### 3.5 Select

Per collection a contextual combinatorial bandit (C²UCB with structured
rewards): arms are surviving candidates; context features are template
weight, estimated selectivity from the sketches, collection row count, index
width, current write rate, and whether an existing index covers a prefix;
the reward observed after activation is measured logical work saved for the
templates whose plan changed. The greedy oracle chooses the super-arm that
maximizes uncertainty-weighted expected benefit (UTune) subject to the
**write budget**: the sum of active-index maintenance costs on the collection
stays under 2× the base write cost, and the count under the per-collection
cap (default 12; typed error names the cheapest index to drop). Exploration
never builds more than one candidate per collection at a time.

### 3.6 Build

A selected index is created **invisible**: the descriptor (if new) is
journaled so all Regions start extracting on the next write; backfill runs
in the proposing Region in `seq` order under the per-tenant background
budget, with a durable watermark so it resumes after restart. First-query
policy while building: small collections get an inline cost-capped scan;
large ones get a **hybrid plan** (index over `seq ≤ watermark` ∪ capped scan
of the remainder) when the remainder is under the scan cap, otherwise the
typed `IndexBuilding { retry_after }` (D-56 wording). Once backfilled the
index is maintained in the same transaction as every write, from the first
write after creation, so there is no window in which it is stale.

### 3.7 Verify

With the index built but still invisible, the proposing Region replays the
collection's tuned templates against a shadow connection that can see it,
each execution capped at (template's past average logical cost × 1.5 + a
floor); exceeding the cap is a regression without finishing. Comparison is a
Welch t-test on logical cost per template over ≥ 20 executions each side
(fewer for rare templates, with a wider confidence requirement). Outcomes:

- every template improves or is unchanged, at least one improves ≥ 20 % →
  **activate**: journal the state change; other Regions build and activate
  on receipt without re-verifying.
- some templates regress → activate **with plan pins**: the regressed
  templates carry a planner hint excluding this index (Oracle's baseline
  idea, implemented as `INDEXED BY`/`NOT INDEXED` rewriting in our planner);
  pins are visible in the audit trail.
- no template improves → **abandon**: drop the shadow, blocklist the
  (template set, candidate) pair for 7 days, keep the descriptor only if
  another candidate needs it.

### 3.8 Monitor and revert

Active indexes keep being measured. If a template's logical cost regresses
significantly after a plan change that references the index, the index is
made invisible immediately (revert is one journaled flag flip; the physical
drop follows after 24 h so a false alarm costs nothing) and the pair is
blocklisted. Because the revert is planner visibility, not a schema change,
it is instant in every Region.

### 3.9 Retire and merge

An index unused by any template for the retirement window (default 7 days,
per-collection configurable) is made invisible, then dropped 24 h later
unless a template used it in between (Azure's warning: dropping hurts rare
queries; the window is measured against the rarest template that ever used
it). Indexes whose column list is a prefix of another are merged: the wider
one is verified against the narrower one's templates before the narrower is
retired. Descriptors with no remaining index and no access rule are retired,
which stops extraction and drops the column on the next schema compaction.

### 3.10 Customer control

Every state transition is audit-trailed and shown in the dashboard with its
evidence (templates, measured before/after, the bandit's score). One-click
**apply** on a recommendation skips selection and goes straight to build →
verify. Per collection: `auto: on | recommend-only | off`, `pin` an index
(never retire), `forbid` a candidate shape. `terrascale index wait` blocks
on the state machine.

## 4. Invariants

1. Zero stale reads: an index visible to the planner is maintained in the
   same transaction as every write. Tested by a lane that writes and
   immediately queries through every active index.
2. No verified regression survives: if the monitor observes a significant
   regression attributable to an index, that index is invisible within one
   observation window.
3. Write budget holds: maintenance cost of visible indexes on a collection
   never exceeds the budget; exceeding it fails the transition, not the write.
4. Convergence: for a stable workload the set of visible indexes reaches a
   fixed point and stays there (no oscillation); measured on E-13 replays.
5. Descriptor and lifecycle state are identical across Regions after
   replication; physical build progress may differ.

## 5. Consequences

- The RavenDB comparison lane (GOAL E-13) adds: templates-to-fixed-point,
  regression count after activation (must be zero), and write throughput at
  the budget ceiling.
- The SQL planner gains: hypothetical-index planning, `NOT INDEXED` pins per
  template, and hybrid watermark plans.
- The write path gains per-column HLL and histogram maintenance; its cost is
  included in "cost per admitted write".
- The dashboard's index page (D-56) shows the state machine and evidence,
  not just a list.

## References

- Das, Grbic, Ilic, Jovandic, Jovanovic, Narasayya, Radulovic, Stikic, Xu, Chaudhuri. *Automatically Indexing Millions of Databases in Microsoft Azure SQL Database.* SIGMOD 2019.
- Chakkappen et al. *Automatic Indexing in Oracle.* PVLDB 18, 2025.
- Perera, Oetomo, Rubinstein, Borovica-Gajic. *DBA Bandits: Self-driving index tuning under ad-hoc, analytical workloads with safety guarantees.* ICDE 2021; *No DBA? No Regret! Multi-Armed Bandits for Index Tuning of Analytical and HTAP Workloads With Provable Guarantees.* IEEE TKDE 2023.
- *UTune: Towards Uncertainty-Aware Online Index Tuning.* arXiv 2601.18199, 2026.
- Schlosser, Kossmann, Boissier. *Efficient Scalable Multi-Attribute Index Selection Using Recursive Strategies* (Extend). ICDE 2019.
- Kossmann, Halfpap, Jankrift, Schlosser. *Magic mirror in my hand, which is the best in the land? An Experimental Evaluation of Index Selection Algorithms.* PVLDB 13, 2020.
- Siddiqui, Jindal, et al. *ISUM: Efficiently Compressing Large and Complex Workloads for Scalable Index Tuning.* SIGMOD 2022.
- Wang et al. *AutoIndexer: A Reinforcement Learning-Enhanced Index Advisor Towards Scaling Workloads.* arXiv 2507.23084, 2025 (workload compression stage only).
- Zhao et al. *RIB: Robust Learning-based Index Benefit Estimation.* SIGMOD 2026 (future option).
- Idreos, Kersten, Manegold. *Database Cracking.* CIDR 2007; Idreos, Manegold, Kuno, Graefe. *Merging What's Cracked, Cracking What's Merged.* PVLDB 2011.
- SQLite. *The SQLite Expert Extension* (`sqlite3_expert`), sqlite.org/expert.html.
