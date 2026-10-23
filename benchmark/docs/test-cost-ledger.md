# Testing cost ledger (all providers, all testing)

One ledger for every attributable testing cost: all threads, all providers, existing test resources, retained storage,
the test host and agent usage. Owner: the 100M backup/PITR program lead. Sources: read-only provider metadata
(`flyctl` listings, GitHub's billing usage API with the existing token's scope), official price pages, this program's
measurements and figures other owners supply. No credential value and no cancelled thread history is read.

Historical observations retain their source dates below; they do not establish current ownership, pricing or runner configuration. Current CI uses GitHub-hosted runners.

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
  plus one Tigris bucket. Compute is not allocated from forecasts.

## Prices (official pages, read 2026-10-12)

| Provider | Item | Price | Verification |
| --- | --- | --- | --- |
| Tigris | Standard storage (global, paid once) / Class A / Class B / egress | $0.02 per GiB-month / $0.005 per 1,000 / $0.0005 per 1,000 / free; no Standard minimum duration | tigrisdata.com/pricing |
| GitHub | Actions Linux minutes (private repo) | $0.006 per minute gross (528 minutes = $3.168 in the usage data) | billing usage API |

## Inventory and month-to-date (2026-10-12)

| Resource | Provider / account | Owner | Verified quantity | Billed this month | Recurring forecast | Unknowns |
| --- | --- | --- | --- | --- | --- | --- |
| GitHub Actions (private repo) | GitHub org `TerraScale` | CI owners | 528 Linux minutes, 17 GB-hours of Actions storage, cache 12.1 GB in 61 entries | **$0 net** ($3.17 gross, fully discounted) | grows with GitHub-hosted runners since #139 | the plan's included minutes; when discounts end |
| Historical CI on self-hosted "alpha" runners (#142, merged 2026-10-12) | host alpha | CI owners | — | not provider-billed as far as seen | unknown (power, hardware) | ownership and incremental cost not verified; GitHub minutes drop while alpha runs CI |
| PR preview buckets | Tigris via Fly, `terrascale-previews-515` | preview owners | 17 buckets | unknown | $0.02 per GiB-month | sizes (not shown by `flyctl storage status`) |
| Dev bucket `ts-terrabase-dev` | Tigris via Fly, `terrascale` | TerraBase owners | 1 bucket | unknown | $0.02 per GiB-month | size |
| Host beta | homelab VM (host services `homelab-dev-vm-helpers`, `tailscale-beta`) | user | 12 CPUs, 14 GB | no provider bill seen | unknown (power, hardware) | ownership and incremental cost not verified |
| AI agent usage | via an API gateway (`ANTHROPIC_BASE_URL`, `CLAUDE_CODE_USE_GATEWAY`); Codex with its own auth | user | — | unknown | unknown | billing mode (subscription or per token) |
| G1 bucket `ts-pitr-test-g1-20261012` (proposed) | Tigris, existing TerraScale account (the organization holding `ts-terrabase-dev`), selected by the user in the UI | this program | not created | $0 | ≤ $1 (approved stop: 5 GB) | user creates it and its bucket-scoped key |
| **Totals** | | | | **$0 verified (GitHub)**; everything else unknown | Recurring costs remain unknown; retained Tigris storage and every unknown above must be accounted for. | |

Remaining budget is **not known**: $200 minus billed amounts that are mostly unknown, minus recurring costs that are
only partly verified, minus the $1 G1 commitment. No headroom is inferred from incomplete historical observations. Until the unknowns are filled, this program commits nothing beyond the approved G1 and
keeps the rest of the cap as reserve.

## Routes still open before asking the user

| Unknown | Route |
| --- | --- |
| Tigris bucket sizes | owners supply them, or the bucket owners list them with their own access |
| Agent gateway billing, beta's incremental cost | owners supply them; otherwise these become user questions |
| GitHub included minutes | an owner with org-billing access |

## Staged forecast (this program and the replication requirement)

| Stage | Forecast | Starts only when |
| --- | --- | --- |
| G1 (approved) | ≤ $1: 1–2 GB stored, ~1k Class A, ~5k Class B | fixed integrated binary green and reviewed; spend monitor ready; user's bucket and key handoff done |
| G2 | about $1 in Tigris storage and requests | G1 clean; beta's memory and disk within limits |
| G3 | Tigris about $5 per month retained plus about $1 in requests; compute only if beta cannot hold it, priced from verified regional rates first | G2 numbers justify it; P11 fixed or enough memory; forecast plus known baseline under $200 |

This ledger remains the single budget authority. Any future three-region proof needs a fresh forecast from verified regional rates and current storage quantities; correctness gates, bucket-scoped credentials, separately approved network-partition faults and the monthly cap still apply. Budget alone starts nothing.

## Monitoring and updates

- During G1 the harness lists the bucket every 10 minutes and stops writing and the node at 4.5 GB stored or a
  projected $0.80.
- Updated at every stage start and end, daily while anything runs, and whenever an owner supplies figures.
