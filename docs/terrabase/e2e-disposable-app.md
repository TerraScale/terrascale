# Three-region acceptance on a disposable Fly app (runbook, not executed)

Status: prepared by the R3 harness owner, 2026-10-12. Nothing here has been created or run. It is the plan for proving
replication across three actual Fly regions without touching production: every step that creates, credentials or
faults something is listed with the approval it needs.

## What it proves, and what it cannot

| Requirement | How | Counts as actual-region proof |
| --- | --- | --- |
| All six directed edges (SJC→GRU, SJC→FRA, GRU→SJC, GRU→FRA, FRA→SJC, FRA→GRU) | `ThreeRegionE2ETests.Write_in_one_region_is_readable_in_the_other_two`, six cases | yes: each edge's writer and reader are named `region/machine@revision` in the run's latency lines |
| Exact machine, region and commit per edge | `TERRABASE_E2E_FLY_REVISION=<sha>`: every request carries `fly-force-instance-id` (Fly routes to that machine or fails, never to another), and every answer must carry the expected `X-TerraBase-Machine` and `X-TerraBase-Region` (`MachinePin`); before any write each machine's `/healthz` must report that machine, region and revision | yes |
| Concurrent writes, convergence, counts and checksums | the rest of `ThreeRegionE2ETests` and `LatticeE2ETests` (concurrent fields, increments, set adds, bursts, 3,000 documents with per-region counts and digests) | yes |
| Crash and restart | `flyctl machine kill <id>` (SIGKILL, a crash) and `flyctl machine restart <id>` (a clean restart) of one region's machine while the others write, then convergence and per-region digests | yes |
| Partition and rejoin | see "Partition" below: a real network partition between two regions' machines, applied inside those machines | yes, once the mechanism's approval is given; a local-process partition is supporting evidence only and is not reported as this |
| Catalog and security state | `GET /catalog-gate` on each machine over 6PN (`open`, `trusted`, `pending`, `digest`): equal digests, every machine trusted | yes |
| Backup (A7YES) | see "Backup" below | yes |

Aliases (`api.`, `<region>.api.terrascale.tech`) are not used: an answer they route elsewhere proves nothing.

## Gates before any cloud start

- The commit under test is on main with its current-head required checks green (Fast, build, analyzers, fmt) and its
  required reviews approved. Slow is recorded as missing evidence while the CI runner's disk failure lasts, never as
  passed.
- It contains #149 (the `revision`/`machine` health fields and `X-TerraBase-Machine`), R3–R7, R10a's restore gate with
  its two remaining release fixes (`PUT …/backup-destination` and `PATCH …/databases/{d}` marked as revocations), and
  Tester B's fixes.
- The user has approved the spend row and the credentials below.

## Resources and cost (reconciled with `.cos/spend-ledger.md`, Pot B experiments)

Prices: Fly's official list (docs.fly.io/about/pricing, read 2026-10-12): shared-cpu-1x 512 MB **$3.69/month** in
iad, billed per second while started; stopped machine $0.15/GB-month of rootfs; volume **$0.15/GB-month** (also while
stopped or detached); private cross-region transfer $0.006/GB (NA/EU) and $0.015/GB (SA); inbound free. Regional
multipliers from the ledger's fact-check: gru ×1.615, sjc ×1.192, fra ×1.154. Tigris $0.02/GB-month.

| Item | Qty | If started 24/7 | Per run (machines started ~3 h) |
| --- | --- | --- | --- |
| shared-cpu-1x 512 MB, one per region (gru, sjc, fra) | 3 | 3.69 × (1.615 + 1.192 + 1.154) = **$14.62/month** | ≈ $0.06 |
| 1 GB volume per machine | 3 | $0.45/month | ≈ $0.002 |
| Stopped rootfs between runs (≈ 1 GB each, estimate) | 3 | ≈ $0.45/month | — |
| Test Tigris bucket (< 1 GB) | 1 | < $0.02/month | ≈ $0 |
| Cross-region private traffic (≈ 1 GB per run, estimate) | — | — | ≈ $0.02 |

Allocation asked: **one Pot B row, $5 reserve** for the whole proof (several runs, machines stopped between runs, the
app destroyed at the end), well under the $150 stop. Pot A (recurring) is not touched: nothing runs past the proof.

Org: **`terrascale-previews-515` is at its 20-machine limit** (Fly 422 since 08:16Z, `.cos/morning-release-matrix.md`),
so the app cannot be created there until previews are cleaned. Options, for the user: clean stale previews (#94, #97
and others, the deploy steward's Q-PREVIEW-CLEANUP), or a new dedicated org (user-only). The production org
`terrascale` is not used: it shares production's private network.

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

Selected invariant: a backup at time T contains every write acknowledged before T, or the restore answers a typed
refusal; never a silent partial result. Run: write N documents and record each acknowledgement; take T after the last
one; restore the database to T into a **new** database of the same app (point-in-time restore); require either all N
documents (equal per-document canonical bodies and the listing digest) or a typed refusal naming why T is not
restorable. A restore that returns some but not all of them fails the run. Test bucket only.

## Teardown (scoped)

Only the disposable app and its own test bucket: `flyctl apps destroy <app>` and `flyctl storage destroy <bucket>` after
the evidence is written to `benchmark/bench/`, each logged in the ledger row. Nothing else is deleted; no production
volume is reset.
