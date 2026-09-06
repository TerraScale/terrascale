---
---
<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<!-- GENERATED from data/pricing.json by scripts/generate-pricing.mjs — do not edit. Draft; publication requires measured economics and owner approval. -->
# Limits

> **DRAFT.** Publication awaits measured unit economics and owner approval.

PITR retention (days): Free 1 / Starter 7 / Pro 30 / Scale 35.

Databases per org: Free 1 / Starter 5 / Pro 25 / Scale soft-unlimited.

- **Max document:** 1 MiB.
- **History retention:** Default 7 days, configurable per collection 1-30 days (plan-capped at the PITR figure), billed as storage.
- **Idle Free databases:** Free databases pause after 30 idle days; a request resumes them. They may be deleted after 90 idle days with advance notices unless deletion protection is enabled. Paid databases are never idle-reaped. The final notice schedule requires owner approval.

## Abuse guards (unbilled, enforced, no upsell UI)

Unbilled, enforced, no upsell UI. CDN-delivered reactive data has no bandwidth guard. Subscribers per view are unlimited on every plan. Reads, queries, and direct member read egress are metered for abuse guards and cost attribution;
exceeding one returns a typed error with the fix, and nothing is billed.

| Guard (per database) | Free | Starter | Pro | Scale |
| --- | --- | --- | --- | --- |
| Read + query requests/s | 100 | 1000 | 5000 | custom |
| Direct member read bandwidth/month | 50 GB | 500 GB | 2 TB | custom |
| Distinct active shared views | 1000 | 10000 | 100000 | custom |
| Optional WebSocket connections | 100 | 1000 | 10000 | custom |
| Webhook endpoints | 3 | 10 | 50 | custom |

Subscribers per view are unlimited on every plan. CDN-delivered reactive data has no bandwidth guard.
