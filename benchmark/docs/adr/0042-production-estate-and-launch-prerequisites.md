---
status: superseded (2026-09-10)
authority: ../../../GOAL.md
supersedes: the release-posture, licence, and soak paragraphs of 0033
superseded-by: the co-hosted member-host topology in ts-orchestrator/CONTEXT.md
---

> **Supersession notice (2026-09-10):** This record's dedicated control-host
> topology is retired. The current launch shape is two member hosts in every
> Region, with a local `ts-orchestrator` on each member machine. One selected
> member in each of GRU, SFO, and FRA hosts a global JetStream control-store
> replica; there are no dedicated control hosts. See
> [`ts-orchestrator/CONTEXT.md`](../../../ts-orchestrator/CONTEXT.md) and
> [`ts-orchestrator/docs/guides/architecture-boundaries.md`](../../../ts-orchestrator/docs/guides/architecture-boundaries.md).
> The remaining launch prerequisites below are retained as historical context
> and are not authority for host count, placement, or control-store topology.

# Production estate and launch prerequisites

## Decision

TerraScale launches on **six OCI Ampere A1 Flex member instances across three
regions**, plus a fixed list of managed services. This ADR is the target
specification for the orchestrator's `add-region` command (GOAL D-52, M3-7)
and the checklist of everything that is not hardware but still blocks the
first customer. Anything not on this page is not needed to go to production;
anything on this page is.

Region identity, fault-domain placement, and the `RegionCode` catalog stay
as ADR 0033 defines them. This ADR replaces 0033's release posture: the
engine is **closed source** (GOAL D-50; the MIT paragraph in 0033 is void),
the launch is **public self-service** (D-23), and the pre-launch soak is
**seven days** on the exact launch digests (D-10, M5-1), not thirty.

## Historical topology (superseded)

The former decision described the following per-region shape:

- **two member hosts**, because RF=2 (D-35) only protects data when writer
  and follower are on different hosts and different fault domains;
The third-host regional control-group requirement is superseded. Each Region
now starts with two admitted member hosts in separate failure domains. Both
run TerraBase, `ts-node`, and a local `ts-orchestrator` under systemd. One
selected member in each of GRU, SFO, and FRA hosts a replica of the permanent
global JetStream R3. Regions added later use that global R3 without joining it.
No dedicated control-host class exists.

## Compute estate (per region)

| Role | Count | Shape (start) | Storage | Notes |
| --- | --- | --- | --- | --- |
| Member host | 2 | A1 Flex, 2 OCPU / 12 GiB | 200–500 GiB block volume, Balanced or Higher Performance | Writer + follower for each database placed here; distinct fault domains. Each also runs `ts-node` and a local `ts-orchestrator`; selected members host global R3 replicas. |

Total at launch: **6 member hosts**. Scaling a region is "add a member host";
a fourth region is one `add-region` run on the same table. All instances are
`aarch64-unknown-linux-gnu`; the build profile is ARM-first (D-43). OS: Ubuntu
LTS minimal, binaries supervised by systemd units, unattended security
updates, `chrony` against OCI NTP (HLC skew guard), no password login, no
public SSH.

## Managed storage and OCI services (per region unless stated)

| Service | Purpose | Configuration |
| --- | --- | --- |
| Object Storage bucket | native TerraBase backup targets, analytics snapshots (D-53), Parquet export | One bucket per region, versioning on, retention rule on, lifecycle per D-29 (segments older than 24 h to Infrequent Access, older than 7 days to Archive), **never replicated across regions** (residency, D-52) |
| Tigris S3-compatible backup storage | Independent backup destination selected by plan/user policy (ADR 0046) | Verify residency-compatible placement, scoped credentials, encryption, logical retention/deletion, and independent restore; one or both are required according to effective plan/user policy |
| Block Volume backup policy | Second layer under native TerraBase backups | Daily, 7-day retention, per member host |
| Vault + Key | Per-database encryption keys (M2-1, ADR 0030) | One master key per region; keys never leave their region |
| IAM (tenancy-wide) | Least privilege | One compartment per region; **instance principals** for member hosts; no API keys on disk; CI gets a scoped user per secret (GOAL O-3) |
| VCN + NSG | Network | Public ingress only on 443 (API) and the QUIC replication port (member ↔ member, source-restricted to the other regions' public IPs); everything else via Tailscale |
| Budgets | Cost cap | Monthly budget with alert at 80 % and 100 %, alert routed to paging |
| Email Delivery (GRU) | Transactional email (D-26) | Approved sender domain, SPF/DKIM/DMARC records in Cloudflare. Submitted over the **HTTPS Email Submission API** (the dashboard Worker cannot open SMTP), authenticated with a dedicated IAM user whose key is a Worker secret |

São Paulo has a single availability domain: members there use distinct
**fault domains**. Frankfurt uses distinct availability domains (ADR 0033).

## Outside OCI

| Piece | Purpose | Notes |
| --- | --- | --- |
| Cloudflare zone `terrascale.tech` (Pro) + Workers Paid | DNS, TLS, WAF, rate rules; `ts-proxy` Worker + KV as the global load balancer (D-58); the **dashboard Worker** on `app.` (D-59); later the edge cache (D-55) | Origin certificates on members; `api.` → `ts-proxy` Worker; `gru|sfo|fra.api.` proxied A records straight to member hosts (pinned SDK traffic); `app.` → dashboard Worker (Workers Assets for static files, service binding to `ts-proxy`, secrets via `wrangler secret`); `*.preview.app.` → per-branch preview deployments; `docs.`, `status.` (D-24). No Cloudflare Load Balancing product. Turnstile on signup. |
| Forgejo Actions runner | Native ARM builds and `make check` in CI | One A1 Flex (2 OCPU / 12 GiB) in GRU outside the orchestrator's control, or a GRU member host if capacity allows |
| Paging | Alerts to a phone | Grafana OnCall or PagerDuty free tier; primary + escalation contact; OCI budget alerts and external probe route here too |
| External probe + status page | Independent view of availability (D-23) | A third-party uptime monitor probing all three regions' `/health` and the dashboard; its public page is `status.terrascale.tech` |
| Stripe live + Stripe Tax | Billing (D-21) | Requires the legal entity (GOAL O-5); webhooks to the dashboard |
| Tailscale | Operator access | The only path to SSH; member hosts join a tailnet; no bastion host |

## Cost envelope

At A1 Flex list prices (≈ $0.01 per OCPU-hour and ≈ $0.0015 per GiB-hour):
six member hosts ≈ $165/month, block volumes and backups ≈ $60–100, Object
Storage < $10 at launch volumes, Cloudflare Pro
≈ $20 if WAF rules are wanted plus Workers Paid $5 (covers both `ts-proxy`
and the dashboard; both are far inside the included requests),
paging/uptime/email ≈ $30. **Roughly
$500–650 per month** for three regions at RF=2, before egress (OCI includes
10 TB/month). A fourth region adds ≈ $130/month. These are estimates for
budgeting; the OCI budget alert is the enforcement.

## Launch prerequisites that are not hardware

Each of these is already a GOAL task; they are listed here so nobody
mistakes "the servers are up" for "we are in production".

1. **Soak**: seven days on the exact launch digests with the fault matrix
   green (M5-1, M4-2).
2. **Restore drill performed**: a region rebuilt from Object Storage alone
   (`add-region` into an empty region, native TerraBase backup restore, catch-up) with
   the time recorded (M3-6, M4-2).
3. **Runbooks** for the ten most likely alerts, each rehearsed once (M4-4).
4. **On-call**: a rotation with a primary and an escalation phone, even if
   both are the owner and one friend; paging tested end to end from a real
   alert, an OCI budget alert, and the external probe (M4-4).
5. **Support and incident comms**: a support email and a Discord, an
   incident template for the status page, a postmortem template.
6. **Legal minimum**: ToS, privacy policy, DPA; a GDPR representative if EU
   customers are accepted at launch; account deletion = crypto-shred (M5-4).
7. **Abuse controls**: card verification (D-17), Turnstile on signup,
   Cloudflare rate rules on auth endpoints, per-key limits (M2-3).
8. **Data handling policy**: log retention (30 days, no document bodies in
   logs), PII fields never in metrics labels, key rotation schedule
   (yearly master, on-demand per database).
9. **Cost guard**: OCI budgets wired to paging; per-org spend caps proven
   (M5-2).
10. **Owner sign-off** on the GOAL launch checklist.

## Consequences

- `add-region` (M3-7) takes this table as input and produces exactly this
  estate; its `--dry-run` output is the runbook.
- The orchestrator's placement model must express two member hosts in distinct
  failure domains as a Region's minimum and refuse to admit a Region below it.
- Local orchestration is co-hosted on every member host. Global R3 replica
  placement is a separate decision and is co-hosted on selected members in
  GRU, SFO, and FRA; no dedicated control-host class may be introduced.
- ADR 0033 remains the authority for `RegionCode`, provider mapping, and SLO
  definitions; its licence and soak paragraphs are void.
- Anything that would add a machine class (dedicated analytics nodes,
  dedicated edge nodes, a bastion) is a new ADR, not a quiet addition.

The native backup rebuild completely removes external Litestream integration.
The budget above predates the policy-selected Tigris copy: recalculate storage,
requests, retrieval, and OCI outbound costs for each supported provider policy before launch.
Restore qualification must recover independently from OCI and from Tigris,
including loss of the other provider (ADR 0046).
