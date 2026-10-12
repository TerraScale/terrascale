# Three-region acceptance on a disposable Fly app (runbook, not executed)

Status: prepared by the R3 harness owner, 2026-10-12. Nothing here has been created or run. It is the plan for proving
replication across three actual Fly regions without touching production: every step that creates, credentials or
faults something is listed with the approval it needs.

## What it proves, and what it cannot

| Requirement | How | Counts as actual-region proof |
| --- | --- | --- |
| All six directed edges (SJC→GRU, SJC→FRA, GRU→SJC, GRU→FRA, FRA→SJC, FRA→GRU) | `ThreeRegionE2ETests.Write_in_one_region_is_readable_in_the_other_two`, six cases | yes: each edge's writer and reader are named `region/machine@revision` in the run's latency lines |
| Exact machine, region and commit per edge | `TERRABASE_E2E_FLY_REVISION=<sha>` (pinned mode): every request carries `fly-force-instance-id`, and every answer must carry the expected `X-TerraBase-Machine` and `X-TerraBase-Region` (`MachinePin`), so an answer from any other machine fails the request. Before any write and again when the run ends, each machine's `/healthz` must report that machine, region and revision. Fly documents that a forced instance has no fallback; that is recorded as unverified until the first disposable run observes it, and `MachinePin` does not rely on it | yes |
| Concurrent writes, convergence, counts and checksums | the rest of `ThreeRegionE2ETests` and `LatticeE2ETests` (concurrent fields, increments, set adds, bursts, 3,000 documents with per-region counts and digests) | yes |
| Crash and restart | `flyctl machine kill <id>` (SIGKILL, a crash) and `flyctl machine restart <id>` (a clean restart) of one region's machine while the others write, then convergence and per-region digests | yes |
| Partition and rejoin | see "Partition" below: a real network partition between two regions' machines, applied inside those machines | yes, once the mechanism's approval is given; a local-process partition is supporting evidence only and is not reported as this |
| Catalog and security state | `GET /catalog-gate` on each machine over 6PN (`open`, `trusted`, `pending`, `digest`): equal digests, every machine trusted | yes |
| Backup (A7YES) | see "Backup" below | supported cuts that restore and match: yes; a typed refusal: safety only, never functional proof |

Aliases (`api.`, `<region>.api.terrascale.tech`) are not used: an answer they route elsewhere proves nothing.

## Target (explicit; checked before any write or fault)

A pinned run (`TERRABASE_E2E_FLY_REVISION=<sha>`) needs every part of its target named; none has a default:

| Variable | Value |
| --- | --- |
| `TERRABASE_E2E_FLY_APP` | the disposable app |
| `TERRABASE_E2E_FLY_URL` | `https://<app>.fly.dev/`, exactly |
| `TERRABASE_E2E_FLY_ORG` | the app's org |
| `TERRABASE_E2E_FLY_NETWORK` | the app's custom private network; required under option A (org `terrascale`), empty otherwise |
| `TERRABASE_E2E_FLY_MACHINES` | `gru=<id>,sjc=<id>,fra=<id>` |

Checks, in order, each stopping the run (`DisposableTarget`, negative controls in `DisposableTargetTests`):

1. **Configuration, before any flyctl call or request:** a missing variable; `ts-terrabase`, `ts-auth`, `ts-dashboard` or
   any `ts-pr-*` app; a URL other than the app's own host (so production's `.fly.dev` hosts, `api.`, `auth.` and the
   regional aliases are refused); the previews' org; org `terrascale` without a network; a region of the run without a
   named machine.
2. **Read-only, before any write or fault:** `flyctl apps list --org <org> --json` must show the app in that org on that
   network (production's apps show network `""`, Fly's default 6PN, so an option-A app on the default network is refused);
   `flyctl machine list --app <app> --json` must show exactly the named machine in each region.
3. **Identity:** each machine's `/healthz` must report its region, machine and the revision, before the first write and
   again when each test's cluster is released (every scenario), so a redeploy or machine change mid-run fails that test.
4. **Only passing scenarios count:** a pinned run fails any scenario still listed in `Gaps` (a strict expected failure)
   before its cluster starts, so no `region/machine@revision` line is printed for it. The rows a run relies on (for the
   six edges, `pair`) must be removed on main first.

Run labels name the mode: `local:<region>`, `fly-unpinned:<region>` (the production e2e project's own collection,
region-pinned only; never acceptance evidence and never machine faults) and `fly-pinned:<region>/<machine>@<revision>`.

## Gates before any cloud start

- The commit under test is on main with its current-head required checks green (Fast, build, analyzers, fmt) and its
  required reviews approved. Slow is recorded as missing evidence while the CI runner's disk failure lasts, never as
  passed.
- It contains #149 (the `revision`/`machine` health fields and `X-TerraBase-Machine`), R3–R7, R10a's restore gate with
  its two remaining release fixes (`PUT …/backup-destination` and `PATCH …/databases/{d}` marked as revocations), and
  Tester B's fixes.
- The `Gaps` rows of every scenario the run reports are removed on main (the pinned run refuses listed ones).
- The user has approved the spend row and the credentials below.

## Resources and cost (estimate for Beta's aggregate testing ledger; not a separate budget)

Beta owns the single aggregate testing ledger (`.cos/spend-ledger.md`, experiments pot). This section is the estimate to
reconcile there before anything is allocated; it is not an allocation.

Official list prices (docs.fly.io/about/pricing, read 2026-10-12): shared-cpu-1x 512 MB **$3.69/month** in iad, billed
per second while started; stopped machine $0.15/GB-month of rootfs; volume **$0.15/GB-month** (also while stopped or
detached); private cross-region transfer $0.006/GB in North America and Europe, $0.015/GB in South America; inbound free;
Tigris $0.02/GB-month. Regional machine multipliers from the ledger's 2026-10-11 fact-check (re-check against Fly's
per-region list before allocation): gru ×1.615, sjc ×1.192, fra ×1.154.

| Region | Machine started (per month / per hour) | 1 GB volume (per month) | Cross-region private traffic out |
| --- | --- | --- | --- |
| gru | $5.96 / $0.0083 | $0.15 | $0.015/GB |
| sjc | $4.40 / $0.0061 | $0.15 | $0.006/GB |
| fra | $4.26 / $0.0059 | $0.15 | $0.006/GB |
| **all three** | **$14.62 / $0.0203** | **$0.45** | ≈ $0.02 per run (≈ 1 GB, estimate) |

Plus a test Tigris bucket (< 1 GB, < $0.02/month) and stopped rootfs between runs (≈ 3 GB, ≈ $0.45/month, estimate).
A run with the three machines started ≈ 3 h costs ≈ $0.06 plus traffic. The ledger entry to propose: the three
machines, volumes and bucket for the duration of the proof, machines stopped between runs, with this per-hour basis.

## Account and isolation (choice for the user; no existing resource is deleted for quota)

- **`terrascale-previews-515` is not usable**: it is at its 20-machine limit (Fly 422 since 08:16Z), and freeing quota
  by deleting existing resources is excluded.
- **Option A (existing, non-preview account): org `terrascale` with a custom private network.** `flyctl apps create
  <app> --org terrascale --network <app>-net` places the app on its own 6PN: Fly documents that apps on separate 6PNs can
  never communicate unless configured to, so it cannot reach production's apps and they cannot reach it. Current use
  (read-only, 21:58Z): 7 machines (ts-terrabase 3, ts-auth 3, one builder). The org's machine limit is not readable with
  existing access: whether +3 fits must be confirmed before creation. Shared with production: the org itself (billing,
  members, tokens with org scope).
- **Option B: a new dedicated org** (user-only to create), on unified billing: full separation, no shared org.

## Credentials (user-executed handoff only)

The app needs, and nothing else: a fresh random `TERRABASE_MASTER_KEY` (its backups' encryption; generated and piped
straight into `flyctl secrets set`, never printed or stored), the test bucket's keys (written by `flyctl storage create`
as the app's secrets), and the e2e API key for a synthetic tenant (`POST /admin/api-keys` on one machine's internal
listener over `flyctl proxy`, as `.cos/e2e-credential-plan.md` does for production, piped into the runner's environment).
No production secret is read or reused. Agents never run the credential steps; the user runs a one-command handoff.

## Partition (precise mechanism; needs approval)

Fly has no partition primitive, and the production image is chiseled (no shell, no `iptables`). The mechanism:

1. A **faults image**, for this app only: the same TerraBase binary and layers plus `iptables`/`ip6tables` and a shell, as a
   separate Dockerfile stage that no production deploy references.
2. A partition between region A's and region B's machines: over `flyctl ssh console` on each, `ip6tables -A INPUT -s
   <other machine's 6PN address> -j DROP` and the same for `OUTPUT -d`. That drops their private-network traffic both ways
   (peer streams, anti-entropy) while Fly Proxy still reaches each machine, so both sides keep accepting writes.
3. Heal: delete the rules (`-D`), or restart the machine (rules do not persist). Then convergence, digests and
   `/catalog-gate` as above.
4. First, a dry check on the disposable app that the kernel accepts the rules (`ip6tables -L`).

Approval needed (network/security): building and deploying a non-production image with network tools, `flyctl ssh`
into the disposable app's machines, and root firewall changes inside them. Nothing applies to production.

## Crash and restart

`flyctl machine kill <id>` (SIGKILL: a crash, the next start takes a new replica epoch) and `flyctl machine restart <id>`
(clean), on one region's machine while the other two write; then convergence, per-region counts and digests, and the
restarted machine's `/catalog-gate` (it opens by pulling from its trusted peers). Disposable app only.

## Backup (A7YES)

Invariant: a backup at time T contains every write acknowledged before T, or the restore answers a typed refusal; never
a silent partial result. A typed refusal passes the **safety** assertion only; it is **not** a successful restore.

Qualification needs both, reported separately:

1. **Supported cuts (functional; must be a frozen, non-empty set):** before the run, fix the cuts that must restore,
   including known fully durable points: a transaction boundary right after an acknowledged commit, and a backup
   checkpoint the backup reports as shipped. Write N documents (recording each acknowledgement and its version), then
   restore each cut into a **new** database. Each must succeed and match: per-document canonical bodies and versions,
   the listing count and digest, and the identity (database id, regions) of what was restored. Any cut that refuses or
   differs fails the run.
2. **Fault and refusal outcomes (safety):** cuts outside the supported set (before the first shipped segment, during an
   injected backup stall). Each either restores exactly the writes acknowledged before it or answers a typed refusal
   naming why. Any partial result fails.

A run in which every cut is refused is **not** green. `BackupQualification.Failures` enforces these rules on the run's
frozen plan and outcomes: no supported cut in the plan, a supported cut that refuses, any restore that differs from the
acknowledged writes (count, digest, a version, source database or regions), an untyped answer, and a missing, repeated or
unplanned cut each fail the run (`BackupQualificationTests`). Test bucket only.

## Teardown (scoped, confirmed per step)

Only the disposable app and its own test bucket, after the evidence is written to `benchmark/bench/`: `flyctl apps
destroy <app>` and `flyctl storage destroy <bucket>`, each confirmed at the time and logged in the ledger row. No other
resource is touched; no production volume is reset; nothing is deleted to make room.
