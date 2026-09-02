---
status: accepted
authority: ../../../GOAL.md
supersedes: the release-posture, licence, and soak paragraphs of 0033
---

# Production estate and launch prerequisites

## Decision

TerraScale launches on **nine OCI Ampere A1 Flex instances across three
regions**, plus a fixed list of managed services. This ADR is the target
specification for the orchestrator's `add-region` command (GOAL D-52, M3-7)
and the checklist of everything that is not hardware but still blocks the
first customer. Anything not on this page is not needed to go to production;
anything on this page is.

Region identity, fault-domain placement, and the `RegionCode` catalog stay
as ADR 0033 defines them. This ADR replaces 0033's release posture: the
engine is **closed source** (GOAL D-50; the MIT paragraph in 0033 is void),
the launch is a **waitlist beta** (D-23), and the pre-launch soak is
**seven days** on the exact launch digests (D-10, M5-1), not thirty.

## Why not three servers

One A1 per region is the development shape. Production needs, per region:

- **two member hosts**, because RF=2 (D-35) only protects data when writer
  and follower are on different hosts and different fault domains;
- **a third host** for the regional control group, because the orchestrator's
  JetStream control store is R3 (D-06) and the third replica must survive
  the loss of either member host.

Three hosts per region is therefore the floor. Nothing else needs its own
machine at launch: the GRU global authority, Prometheus, Grafana, and
Alertmanager run as containers under the orchestrator on the GRU hosts. The
dashboard does **not** run on OCI at all: it is a Cloudflare Worker (GOAL
D-59) whose only state is a TerraScale database placed in every region, so
the member hosts carry nothing but terrabase.

## Compute estate (per region)

| Role | Count | Shape (start) | Storage | Notes |
| --- | --- | --- | --- | --- |
| Member host | 2 | A1 Flex, 4 OCPU / 24 GiB | 200–500 GiB block volume, Balanced or Higher Performance | Writer + follower for each database placed here; distinct fault domains; also the Postgres-wire listener. Nothing else (no dashboard, D-59) |
| Control host | 1 | A1 Flex, 1 OCPU / 6 GiB | 50 GiB boot | Third JetStream replica, regional control group; in GRU also the global authority, Prometheus, Grafana, Alertmanager |

Total at launch: 6 member hosts + 3 control hosts = **9 instances**. Scaling
a region is "add a member host"; a fourth region is one `add-region` run on
the same table. All instances are `aarch64-unknown-linux-gnu`; the build
profile is ARM-first (D-43). OS: Oracle Linux 9 or Ubuntu 24.04 ARM, rootless
podman under systemd user units, unattended security updates, `chrony`
against OCI NTP (HLC skew guard), no password login, no public SSH.

## Managed OCI services (per region unless stated)

| Service | Purpose | Configuration |
| --- | --- | --- |
| Object Storage bucket | Litestream targets, analytics snapshots (D-53), Parquet export | One bucket per region, versioning on, retention rule on, **never replicated across regions** (residency, D-52) |
| Block Volume backup policy | Second layer under Litestream | Daily, 7-day retention, per member host |
| Vault + Key | Per-database encryption keys (M2-1, ADR 0030) | One master key per region; keys never leave their region |
| OCIR (tenancy-wide) | Image registry | Immutable tags by digest; the orchestrator deploys digests only |
| IAM (tenancy-wide) | Least privilege | One compartment per region; **instance principals** for members and control hosts; no API keys on disk; CI gets a scoped user per secret (GOAL O-3) |
| VCN + NSG | Network | Public ingress only on 443 (API), the QUIC replication port (member ↔ member, source-restricted to the other regions' public IPs), and the Postgres-wire port; everything else via Tailscale |
| Budgets | Cost cap | Monthly budget with alert at 80 % and 100 %, alert routed to paging |
| Email Delivery (GRU) | Transactional email (D-26) | Approved sender domain, SPF/DKIM/DMARC records in Cloudflare. Submitted over the **HTTPS Email Submission API** (the dashboard Worker cannot open SMTP), authenticated with a dedicated IAM user whose key is a Worker secret |

São Paulo has a single availability domain: members there use distinct
**fault domains**. Frankfurt uses distinct availability domains (ADR 0033).

## Outside OCI

| Piece | Purpose | Notes |
| --- | --- | --- |
| Cloudflare zone `terrascale.tech` (Pro) + Workers Paid | DNS, TLS, WAF, rate rules; `ts-proxy` Worker + KV as the global load balancer (D-58); the **dashboard Worker** on `app.` (D-59); later the edge cache (D-55) | Origin certificates on members; `api.` → `ts-proxy` Worker; `gru|sfo|fra.api.` proxied A records straight to member hosts (pinned SDK traffic); `app.` → dashboard Worker (Workers Assets for static files, service binding to `ts-proxy`, secrets via `wrangler secret`); `*.preview.app.` → per-branch preview deployments; `docs.`, `status.` (D-24). No Cloudflare Load Balancing product. Turnstile on signup. |
| Postgres-wire exposure (D-54) | BI tools over TCP | **Direct**: `pg.<region>.api.terrascale.tech` resolves to the member hosts' public IPs (DNS-only, grey-cloud); TLS terminated by the member with a Let's Encrypt certificate; NSG allows the port from anywhere; per-key connection limits apply. Cloudflare Spectrum is not used at launch. |
| Forgejo Actions runner | Native ARM builds and `make check` in CI | One A1 Flex (2 OCPU / 12 GiB) in GRU outside the orchestrator's control, or the GRU control host if capacity allows |
| Paging | Alerts to a phone | Grafana OnCall or PagerDuty free tier; primary + escalation contact; OCI budget alerts and external probe route here too |
| External probe + status page | Independent view of availability (D-23) | A third-party uptime monitor probing all three regions' `/health` and the dashboard; its public page is `status.terrascale.tech` |
| Stripe live + Stripe Tax | Billing (D-21) | Requires the legal entity (GOAL O-5); webhooks to the dashboard |
| Tailscale | Operator access | The only path to SSH; members and control hosts join a tailnet; no bastion host |

## Cost envelope

At A1 Flex list prices (≈ $0.01 per OCPU-hour and ≈ $0.0015 per GiB-hour):
six member hosts ≈ $330/month, three control hosts ≈ $45, block volumes and
backups ≈ $60–100, Object Storage < $10 at launch volumes, Cloudflare Pro
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
   (`add-region` into an empty region, Litestream restore, catch-up) with
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
- The orchestrator's placement model must express "two member hosts in
  distinct fault domains + one control host" as a region's minimum and
  refuse to admit a region below it.
- ADR 0033 remains the authority for `RegionCode`, provider mapping, and SLO
  definitions; its licence and soak paragraphs are void.
- Anything that would add a machine class (dedicated analytics nodes,
  dedicated edge nodes, a bastion) is a new ADR, not a quiet addition.
