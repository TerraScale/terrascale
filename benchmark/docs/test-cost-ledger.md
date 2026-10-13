# Testing cost ledger (all providers, all testing)

One ledger for every attributable testing cost: all threads, all providers, existing test resources, retained storage,
the test host and agent usage. Owner: the 100M backup/PITR program lead. Sources: read-only provider metadata
(`flyctl` listings, GitHub's billing usage API with the existing token's scope), official price pages, this program's
measurements and figures other owners supply. No credential value and no cancelled thread history is read.

CI runner decision (2026-10-13): keep self-hosted alpha. Its five slots share 44 visible CPUs; each test job gets
8 CPUs and 8 .NET server-GC heaps. [PR #212](https://github.com/TerraScale/terrascale/pull/212) tracks the sizing
implementation and hosted qualification. The inventory below retains its observation date.

## Rules (user, 2026-10-12)

- **Cap: US$200 per calendar month, total across all testing services.** Ask the user before any action whose
  forecast would take the month past it.
- Nothing is excluded by assumption. A zero is recorded only when verified (included in a plan, or owned hardware
  with no provider bill); otherwise the amount is **unknown**.
- Monitored stop thresholds are this program's own stops, not provider billing caps. Retained storage keeps accruing
  until a separately approved cleanup.
- The cap is no waiver: exact account and bucket identity, the user-executed key handoff, no persistent credential
  creation by agents, no deletion without approval, unchanged production acknowledgment and availability semantics.
- Stages go G1 → G2 → G3 only when the previous stage's measurements justify the next. G1 stays minimal: host beta
  plus one Tigris bucket. No Fly machines or volumes are created from forecasts.

## Prices (official pages, read 2026-10-12)

| Provider | Item | Price | Verification |
| --- | --- | --- | --- |
| Tigris | Standard storage (global, paid once) / Class A / Class B / egress | $0.02 per GiB-month / $0.005 per 1,000 / $0.0005 per 1,000 / free; no Standard minimum duration | tigrisdata.com/pricing |
| Fly | Started machine, base table (iad, ewr): shared-cpu-1x 1GB / shared-cpu-2x 2GB / performance-1x 2GB / performance-2x 4GB; added RAM | $6.70 / $13.39 / $33.00 / $66.00 per month; $6.00 per GB-month | docs.fly.io/about/pricing page text |
| Fly | Machine prices in gru, sjc, fra | **unverified**: the static table covers the base regions only; regional prices sit behind the page's selector | to read from the selector before any allocation |
| Fly | Stopped or suspended machine | $0.15 per GB-month of rootfs | page text |
| Fly | Volume / volume snapshot | $0.15 per GB-month provisioned (also while detached or the machine is stopped) / $0.08 per GB-month stored, first 10 GB free | page text |
| Fly | Internet egress (NA, EU / South America) / private cross-region (NA, EU / South America); inbound free | $0.02 / $0.04 per GB; $0.006 / $0.015 per GB | page text; older organizations without granular pricing pay the internet rate |
| GitHub | Actions Linux minutes (private repo) | $0.006 per minute gross (528 minutes = $3.168 in the usage data) | billing usage API |

## Inventory and month-to-date (2026-10-12)

| Resource | Provider / account | Owner | Verified quantity | Billed this month | Recurring forecast | Unknowns |
| --- | --- | --- | --- | --- | --- | --- |
| GitHub Actions (private repo) | GitHub org `TerraScale` | CI owners | 528 Linux minutes, 17 GB-hours of Actions storage, cache 12.1 GB in 61 entries | **$0 net** ($3.17 gross, fully discounted) | grows with GitHub-hosted runners since #139 | the plan's included minutes; when discounts end |
| CI on self-hosted "alpha" runners (#142, merged 2026-10-12; a `CI_RUNNER` switch selects them) | host alpha | CI owners | — | not provider-billed as far as seen | unknown (power, hardware) | ownership and incremental cost not verified; GitHub minutes drop while alpha runs CI |
| PR preview volumes | Fly org `terrascale-previews-515` | preview owners | 17 × 1 GB (gru) | unknown | **$2.55 per month** while retained | — |
| PR preview machines | same | preview owners | 15 stopped shared-cpu-1x 1GB (gru); 2 apps without machines | unknown | rootfs × $0.15 per GB-month while stopped; runtime when previews run | rootfs size; runtime hours this month; gru price |
| PR preview buckets | Tigris via Fly, `terrascale-previews-515` | preview owners | 17 buckets | unknown | $0.02 per GiB-month | sizes (not shown by `flyctl storage status`) |
| Retained previews of merged or closed PRs | same | preview owners | 8 apps (PRs 79, 88, 94, 97, 106, 111, 112, 115) with their volumes and buckets | — | part of the rows above | why teardown did not run; cleanup needs owner approval |
| Remote builder `fly-builder-late-star-2225` | Fly org `terrascale` | deploy owners | 50 GB volume (gru), 1 stopped shared-cpu-8x 8GB | unknown | **$7.50 per month** for the volume + rootfs | share attributable to testing (previews) vs production deploys |
| Dev bucket `ts-terrabase-dev` | Tigris via Fly, `terrascale` | TerraBase owners | 1 bucket | unknown | $0.02 per GiB-month | size |
| Host beta | homelab VM (host services `homelab-dev-vm-helpers`, `tailscale-beta`) | user | 12 CPUs, 14 GB | no provider bill seen | unknown (power, hardware) | ownership and incremental cost not verified |
| AI agent usage | via an API gateway (`ANTHROPIC_BASE_URL`, `CLAUDE_CODE_USE_GATEWAY`); Codex with its own auth | user | — | unknown | unknown | billing mode (subscription or per token) |
| G1 bucket `ts-pitr-test-g1-20261012` (proposed) | Tigris, existing TerraScale account (the organization holding `ts-terrabase-dev`), selected by the user in the UI | this program | not created | $0 | ≤ $1 (approved stop: 5 GB) | user creates it and its bucket-scoped key |
| **Totals** | | | | **$0 verified (GitHub)**; everything else unknown | Testing-attributable verified recurring: **$2.55 per month** (preview volumes). Shared builder volume $7.50 per month with an unknown testing share. Plus every unknown above. | |

Remaining budget is **not known**: $200 minus billed amounts that are mostly unknown, minus recurring costs that are
only partly verified, minus the $1 G1 commitment. The verified volume run-rate says nothing about the rest, so no
headroom is inferred from it. Until the unknowns are filled, this program commits nothing beyond the approved G1 and
keeps the rest of the cap as reserve.

## Routes still open before asking the user

| Unknown | Route |
| --- | --- |
| Fly billed amounts and machine runtime | owners of previews and deploys supply figures; `flyctl` has no billing listing and the API token is not read |
| Tigris bucket sizes | owners supply them, or the bucket owners list them with their own access |
| Agent gateway billing, beta's incremental cost | owners supply them; otherwise these become user questions |
| GitHub included minutes | an owner with org-billing access |

## Staged forecast (this program and the replication requirement)

| Stage | Forecast | Starts only when |
| --- | --- | --- |
| G1 (approved) | ≤ $1: 1–2 GB stored, ~1k Class A, ~5k Class B | fixed integrated binary green and reviewed; spend monitor ready; user's bucket and key handoff done |
| G2 | about $1 in Tigris storage and requests | G1 clean; beta's memory and disk within limits |
| G3 | Tigris about $5 per month retained plus about $1 in requests; compute only if beta cannot hold it, priced from verified regional rates first | G2 numbers justify it; P11 fixed or enough memory; forecast plus known baseline under $200 |
| Three-region replication, six directed paths (R3, PR #151; Tester A reviewing) | R3's plan: 3 × shared-cpu-1x 512 MB (sjc, gru, fra) plus 3 × 1 GB volumes, as a disposable app.
- **Machines:** verified from the official page source (read 2026-10-12): base $3.69/month; region markups sjc 1.192307692, gru 1.615384615, fra 1.153846154. That gives $4.40 + $5.96 + $4.26 = **$14.62/month if left started all month**, which matches R3's claim, or about $0.0203/hour for all three.
- **Volumes:** $0.45/month (3 × 1 GB × $0.15), prorated.
- **Stopped machines:** rootfs costs $0.15/GB-month.
- **Traffic:** private cross-region costs $0.006/GB (NA/EU) and $0.015/GB (South America), but only on granular pricing; whether the org is on it is unverified. Without it, public egress rates apply. The replication test moves MBs, so this is cents.
- **Tigris:** the run's bucket objects at the prices above, also cents.
- **Reserve:** R3 proposes $5 for the whole proof, which buys about 200 hours of three started machines plus the volumes. Recorded here as a forecast, **not allocated**.
- **Quota blocker:** `terrascale-previews` is at its 20-machine limit, per R3. Three new machines need a quota increase or an exactly approved cleanup (not authorized). The production org is not allowed.
- **Single authority:** this ledger. R3's `.cos/spend-ledger.md` is an input to reconcile, not a second budget authority. | Correctness prerequisites pass (fixed integrated binary green, independent review, O6 matrix); PR #151 reviewed; #149 deployed (R3's identity checks need it); machine quota resolved without unapproved cleanup; credentials by user handoff only; network-partition faults separately approved; month total under the cap. Budget alone starts nothing. |

## Monitoring and updates

- During G1 the harness lists the bucket every 10 minutes and stops writing and the node at 4.5 GB stored or a
  projected $0.80.
- Updated at every stage start and end, daily while anything runs, and whenever an owner supplies figures.
