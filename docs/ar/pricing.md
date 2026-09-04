---
---
<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<!-- GENERATED from data/pricing.json by scripts/generate-pricing.mjs — do not edit. Source: GOAL.md D-16/D-17/D-21/D-25/D-64 (L-1 draft). -->
# Pricing

> **DRAFT (L-1).** Values mirror GOAL.md D-16/D-17/D-21/D-25/D-64. Confirm
> with the owner before publication (O-6); every published number needs a
> measured unit cost behind it (D-61). Regenerated when measurements land.
>
> **Pay for writes and storage; everything else is free.** Reads, queries,
> egress, CDN delivery, fan-out, subscribers, and webhook delivery are free
> and unbilled. Reads, queries, and direct member read egress are metered for
> abuse guards and cost attribution; a guard is never a charge. A write is billed once no matter how many views or
> subscribers it reaches (D-22). We never claim infrastructure is free:
> replicating a write to another selected region costs +1 write per region,
> shown up front.

## What you pay for

- **Writes.** One create/update/patch/delete of <= 4 KiB (ceil(bytes / 4 KiB)). Replicating a write to another selected region = +1 write per region, shown up front.
- **Storage.** Logical bytes (canonical bytes of current documents + retained history, TTL-aware, measured in byte-seconds) x regions, at $/GB-month.
- Not billed as storage: indexes, WAL, backups.
- Free and unbilled: reads, queries, egress, CDN delivery, fan-out, concurrent subscribers, WebSocket connections, webhook delivery, catch-up and replay, TTL expiry.
- Metered for abuse guards and cost attribution (never billed): reads, queries, direct member read egress.

## Plans

|  | Free | Starter | Pro | Scale |
| --- | --- | --- | --- | --- |
| price | $0 | $5 | $50 | $500 |
| writes | 100k writes/month | 1M writes | 100M writes | PAYG (support-and-compliance fee) |
| storage | 100 MB | 5 GB | 25 GB | PAYG |
| databases | 1 | 5 | 25 | soft-unlimited |
| pitrDays | 1 | 7 | 30 | 35 |

Overage and PAYG: **$1 per 1M writes**, **$0.20 per GB-month per region** (Both may only go down.)

**Branch allowance.** Every org, on any plan, gets 100k writes/month + 1 GB shared across all of its branches (dev branches included) on top of plan quotas; branches beyond it bill like databases.

**Free tier.** 1 database, 100 MB, **100,000 writes/month**, up to 3 regions, community support. Card required at signup (Radar + rate limits). First paid month free if subscribing during onboarding.

**Scale.** Priority support, SOC 2 report access, security questionnaires, DPA/SLA.

**Idle Free databases.** A Free database with no request for 30 days is paused (snapshot kept); at 90 days idle, after two more emails, it is deleted. Paid plans are never reaped (D-76).

See also: [Limits](./limits.md).
