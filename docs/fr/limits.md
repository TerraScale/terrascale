---
---
<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<!-- GENERATED from data/pricing.json by scripts/generate-pricing.mjs — do not edit. Source: GOAL.md D-16/D-17/D-21/D-25/D-64 (L-1 draft). -->
# Limits

> **DRAFT (L-1).** Values mirror GOAL.md D-25/D-64. Confirm with the owner
> before publication (O-6). Regenerated when measurements land.

PITR retention (days): Free 1 / Starter 7 / Pro 30 / Scale 35.

Databases per org: Free 1 / Starter 5 / Pro 25 / Scale soft-unlimited.

- **Max document:** 1 MiB.
- **History retention:** Default 7 days, configurable per collection 1-30 days (plan-capped at the PITR figure), billed as storage.
- **Idle Free databases (D-76):** A Free database with no request for 30 days is paused (snapshot kept); at 90 days idle, after two more emails, it is deleted. Paid plans are never reaped (D-76).

## Abuse guards (unbilled, enforced, no upsell UI)

Unbilled, enforced, no upsell UI (D-64). CDN-delivered reactive data has no bandwidth guard. Subscribers per view are unlimited on every plan. Reads, queries, and direct member read egress are metered for abuse guards and cost attribution;
exceeding one returns a typed error with the fix, and nothing is billed.

| Guard (per database) | Free | Starter | Pro | Scale |
| --- | --- | --- | --- | --- |
| Read + query requests/s | 100 | 1000 | 5000 | custom |
| Direct member read bandwidth/month | 50 GB | 500 GB | 2 TB | custom |
| Distinct active shared views | 1000 | 10000 | 100000 | custom |
| Optional WebSocket connections | 100 | 1000 | 10000 | custom |
| Webhook endpoints | 3 | 10 | 50 | custom |

Subscribers per view are unlimited on every plan. CDN-delivered reactive data has no bandwidth guard.
