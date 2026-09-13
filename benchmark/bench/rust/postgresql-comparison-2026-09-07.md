# PostgreSQL baseline audit — 2026-09-07

**Usable PostgreSQL baselines: 0 of 20 retained receipts.** The retained measurements cannot establish TerraBase/PostgreSQL parity, a speed ratio, or a numerical optimization target. This audit reads existing files only; it runs no PostgreSQL, other database, SDK, or live benchmark. All historical receipts remain unchanged.

The historical `bench/src/history.rs` validator required complete valid samples and assigned SHA-256 run, candidate, workload, and topology identities. Comparison also requires matching configuration, controlled host, time window, workload policy, boundary, durability, and configuration provenance. Its latest-attempt selection does not fall back to an earlier successful measurement after a failure. A receipt's `complete` status alone does not establish admission.

## Retained baseline inventory

“Missing contract” means no `comparability` object. “Unassigned identity” means all four identity digests are `unassigned`; direct PostgreSQL also has an unassigned run group. “Failed” means connection failure before identity capture, with zero samples. Every row below is **unusable as a baseline**. Older successful direct-PG attempts are superseded in latest-attempt rendering and remain historical evidence only.

| Retained receipt | Observed time (UTC) | Raw status | Admission reason |
|---|---|---|---|
| [postgresql-read](results/postgresql-read.json) | 08-30 15:43:35 | legacy; status absent | Missing contract |
| [postgresql-write](results/postgresql-write.json) | 08-30 15:43:39 | legacy; status absent | Missing contract |
| [postgresql-query](results/postgresql-query.json) | 08-30 15:44:02 | legacy; status absent | Missing contract |
| [postgresql-mixed](results/postgresql-mixed.json) | 08-30 15:44:06 | legacy; status absent | Missing contract |
| [postgresql-read-1788135180812017910-3786857-0](results/postgresql-read-1788135180812017910-3786857-0.json) | 08-31 00:13:00 | failed | Failed; no measurements |
| [postgresql-write-1788135182670212210-3831205-0](results/postgresql-write-1788135182670212210-3831205-0.json) | 08-31 00:13:02 | failed | Failed; no measurements |
| [postgresql-query-1788135183790116747-3831866-0](results/postgresql-query-1788135183790116747-3831866-0.json) | 08-31 00:13:03 | failed | Failed; no measurements |
| [postgresql-mixed-1788135184461262373-3832326-0](results/postgresql-mixed-1788135184461262373-3832326-0.json) | 08-31 00:13:04 | failed | Failed; no measurements |
| [postgresql-read-1788135427431720793-3917375-0](results/postgresql-read-1788135427431720793-3917375-0.json) | 08-31 00:17:05 | complete | Unassigned identity |
| [postgresql-write-1788135478419299488-3920905-0](results/postgresql-write-1788135478419299488-3920905-0.json) | 08-31 00:17:16 | complete | Unassigned identity |
| [postgresql-query-1788135487870053061-3938326-0](results/postgresql-query-1788135487870053061-3938326-0.json) | 08-31 00:18:06 | complete | Unassigned identity |
| [postgresql-mixed-1788135508497306348-3943660-0](results/postgresql-mixed-1788135508497306348-3943660-0.json) | 08-31 00:18:15 | complete | Unassigned identity |
| [postgresql-read-1788137614741020195-272364-0](results/postgresql-read-1788137614741020195-272364-0.json) | 08-31 00:53:34 | failed | Failed; no measurements |
| [postgresql-write-1788137614935028280-272475-0](results/postgresql-write-1788137614935028280-272475-0.json) | 08-31 00:53:34 | failed | Failed; no measurements |
| [postgresql-mixed-1788137615313202308-272578-0](results/postgresql-mixed-1788137615313202308-272578-0.json) | 08-31 00:53:35 | failed | Failed; no measurements |
| [postgresql-query-1788137615123935909-272552-0](results/postgresql-query-1788137615123935909-272552-0.json) | 08-31 00:53:35 | failed | Failed; no measurements |
| [pg-fullstack-read-1788187005634601753-306703-0](results/pg-fullstack-read-1788187005634601753-306703-0.json) | 08-31 14:36:40 | complete | Unassigned identity |
| [pg-fullstack-query-1788187017599762987-309854-0](results/pg-fullstack-query-1788187017599762987-309854-0.json) | 08-31 14:36:50 | complete | Unassigned identity |
| [pg-fullstack-write-1788187050721866209-310755-0](results/pg-fullstack-write-1788187050721866209-310755-0.json) | 08-31 14:37:02 | complete | Unassigned identity |
| [pg-fullstack-mixed-1788187073308144008-328055-0](results/pg-fullstack-mixed-1788187073308144008-328055-0.json) | 08-31 14:37:39 | complete | Unassigned identity |

## Comparison with the September 7 TerraBase run

This snapshot covers the six `terrabase-*.json` receipts initially present in [run-20260907](results/run-20260907). Later TerraBase receipts can improve TerraBase's own evidence but cannot repair missing PostgreSQL provenance.

| Dimension | Retained PostgreSQL / pg-fullstack | TerraBase run-20260907 | Consequence |
|---|---|---|---|
| Workload configuration | 1,000 seeds; 10,000 operations/sample; 5 samples; concurrency 16; 1,000 warmups | Full read uses the same settings; small diagnostic runs use different settings | Only full read matches numerical configuration; this is insufficient for admission |
| Payload and operations | Contract-bearing rows declare 256-byte seed payload, seed IDs, measured write IDs, and 50% read / 30% write / 20% query mix | Same declared `terrabase-se15-workload-v1` policies | Labels agree, but old workload/source digests are unassigned; legacy aliases do not record these policies |
| Durability | Contract-bearing rows declare `synchronous_commit=on`, `fsync=on`, `full_page_writes=on`, autocommit acknowledgement | `LocalDurability::Synced` acknowledged commits | Both declare durable acknowledgement, but semantics/provenance do not match the validator contract; no matched durability experiment |
| Resource envelope | No CPU quota, cpuset, or memory-limit receipt attached to these PG JSON files; 28 logical host CPUs reported | [Member snapshot](results/run-20260907/member-before.txt): CPU quota 200000/100000, cpuset 0–1, memory 12 GiB | Cannot establish equal resources from host CPU count or the historical `envelope-rebaseline` label |
| Hardware and kernel | Linux 7.2.1; host identity absent in aliases, `Linux 7.2.1;logical-cpus=28` for direct PG, `terrabase-dev-28core` for fullstack | Linux 7.2.3; 28 logical host CPUs; `mariogk-dev;fenced-member=GRU` | No matching controlled host identity; CPU model, storage, contention, and placement equivalence unproven |
| Client/server boundary | Direct PG: prepared PostgreSQL TCP statements. Fullstack: HTTP/JSON → pgrest → pgbouncer transaction pooling → PostgreSQL | HTTP/JSON API with Bearer key; loopback without TLS; `diagnostic-qualification` boundary | Direct PG omits API work; fullstack includes HTTP but is a different service path; diagnostic TerraBase rows cannot join a decision-grade comparison |
| Version and source | PostgreSQL 18.6 observed in successful receipts; reference pin labels; no assigned candidate digest | `terrabase-0.1.0`; initial candidate digest `43bc943664898c841b48ca490b7ecad31864cfc58721b26e2ff1ac0a2c4d6935` | Version strings do not prove matching artifacts/topology; historical identities cannot be reconstructed from current source |
| Time and noise | August 30–31 UTC; old windows 496704 / 496718 where recorded; nominal 5% noise setting | September 7–8 UTC; different run groups/windows; nominal 5% noise setting | No controlled shared time window. A configured 5% band is not proof of stability; one-sample diagnostic runs cannot estimate between-sample variation |

## Optimization target

| Requested comparison | Legitimate numerical target | Evidence needed |
|---|---|---|
| Read / write / query / mixed versus direct PostgreSQL | Unavailable | A valid retained reference with matched experiment identities, resources, workload, durability, time window, and an explicitly justified common boundary |
| Read / write / query / mixed versus PostgreSQL fullstack | Unavailable | The same controls, including the service and pooling configuration; the current complete receipts fail identity admission |

TerraBase's retained throughput and latency can guide its own bottleneck investigation. They must keep their diagnostic or failed classification and exact workload settings. No historical PG receipt is promoted to a baseline, no failed sample is treated as zero throughput, and no PostgreSQL rerun is implied by this report.
