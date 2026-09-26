# Query-independent Von selection qualification

The independent 2,000-document qualification completed at 2026-09-24 04:51 UTC. It used three fresh trials; each trial compared no secondary index, deterministic schema-statistics advice, and a local `von-1.2.0` attempt. All arms used the same document seed, the same two held-out predicates drawn only after advice and index application, a two-index limit, 8 MiB retained/transient physical caps, and a 4 MiB logical cap per collection and tenant. Every first and repeated query passed exact document-ID checks. The role descriptions and both seeds were recorded before inference in [selection-plan.json](query-independent-von-selection-20260924T0454Z/selection-plan.json).

| Trial | Von advisor wall time | Returned model | Input/output tokens | Model choice | Applied advisor | Exact-ID result |
| ---: | ---: | --- | ---: | --- | --- | --- |
| 1 | 30,063.4 ms | no response before deadline | unreported | unavailable | deterministic fallback (`von-timeout`) | pass |
| 2 | 30,001.9 ms | no response before deadline | unreported | unavailable | deterministic fallback (`von-timeout`) | pass |
| 3 | 29,323.6 ms | `von-1.2.0` | 1,129 / 9 | `abstain`, probability 0.4377, choice confidence 0.297 | deterministic fallback (`von-uncertain-or-abstained`) | pass |

No accepted Von recommendation occurred, so no row is labeled Von-selected or credited with Von's index choices. The model's third-trial candidate suitability probabilities were 0.3439–0.3584, below the adapter's 0.8 acceptance threshold. The deterministic fallback chose `account.lookupId` and `payload` in each indexed arm; `account.lookupId` served its held-out equality predicate, while the held-out region/queue predicates scanned. These are observations for this workload, not a benefit claim for Von. Von token cost is **unpriced**: the local service has no configured token price, and the raw receipt stores `null` cost rather than the adapter's zero placeholder.

The two timeouts are compatible with resource pressure but cannot be causally assigned from client timing alone. The same local server process, PID 2131099, remained healthy after this run (`GET /health` returned `ok`, service version `1.2.1`, engine `von-1.2`). A read-only process snapshot after the run showed 1,550,524 KiB RSS, 2,682,872 KiB swap, and a 4,053,324 KiB high-water RSS. Earlier [Von resource measurements](von-index-advisor-20260923/von-resource-warm.md) for smaller two-candidate/five-question requests reported 10,842.6 ms steady median and 14,664.0 ms steady p95, with prior cold-run deadline misses documented in [the cold report](von-index-advisor-20260923/von-resource.md). This qualification sent four candidates and nine questions; no server-side CPU/RSS was captured during its individual calls.

The first attempt, [T0450Z](query-independent-von-selection-20260924T0450Z/run-receipt.json), failed when the default 1 MiB collection logical-byte cap refused a snapshot build. Its failed receipt is preserved. The completed [T0454Z summary](query-independent-von-selection-20260924T0454Z/von-selection.md), [raw evidence](query-independent-von-selection-20260924T0454Z/von-selection-receipt.json), and [run receipt](query-independent-von-selection-20260924T0454Z/run-receipt.json) are separate from the previous held-out campaign.

| Artifact | SHA-256 |
| --- | --- |
| `selection-plan.json` | `6610faf8520e54bf5773efe656c44308fa2ac59c5bca3ac5447d4b7cfb436643` |
| `von-selection-receipt.json` | `c24312f2bc1be400e1104a6f62373d3ebfd6f98c9eb26b590d3c13ba4f9846f1` |
| `von-selection.md` | `5f6798f17bb3cda5ed63e1e65c23a2a0e13ee15c5f1ba8befd1bd64aacea4ccf` |
| `run-receipt.json` | `313d904f925e4495c7f25c0389548d898d330bab1ac43b5fead54626923f571f` |
| Loaded TerraBase DLL | `235905372a26957de2f8f74f197291915d1d2ec73e68adb95db16a7bdc685e3e` |
| Benchmark DLL | `98be40a991bc8dcd08e251b11f8102211822c0e3ffb6c5cb3c3d54f155d18ee7` |

The full Release benchmark build passed with zero warnings and errors immediately before this completed run. The run receipt records the live subject source digest `9cc7e63b959dee6e6a17a6b842e4fc6f07bcb7fbda5ce85dd7eb56b3862d0619`. No old held-out receipts or TerraBase production files were changed by this qualification.
