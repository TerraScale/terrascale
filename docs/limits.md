<!-- GENERATED from data/canonical/pricing.json (https://terrascale.tech/pricing.json) and data/policies.json by scripts/generate-pricing.mjs — do not edit. -->
# Limits

PITR retention (days): Hobby 1 / Starter 7 / Pro 30 / Enterprise 35.

Databases per org: Hobby 1 / Starter 5 / Pro 25 / Enterprise Custom.

- **Max document:** 1 MiB.
- **History retention:** Default 7 days, configurable per collection 1-30 days (plan-capped at the PITR figure), counted as storage.
- **Idle Hobby databases:** Hobby databases pause after 30 idle days; a request resumes them. They may be deleted after 90 idle days with advance notices unless deletion protection is enabled. Paid databases are never idle-reaped. The final notice schedule requires owner approval.

## Abuse guards (unbilled safety limits)

Guards are safety limits enforced alongside plan allowances, with no upsell UI. Exceeding one returns a typed error with the fix and is never billed. CDN-delivered reactive data has no bandwidth guard. Subscribers per view are unlimited on every plan.

| Guard (per database) | Hobby | Starter | Pro | Enterprise |
| --- | --- | --- | --- | --- |
| Read + query requests/s | 100 | 1,000 | 5,000 | custom |
| Direct member read bandwidth/month | 50 GB | 500 GB | 2 TB | custom |
| Distinct active shared views | 1,000 | 10,000 | 100,000 | custom |
| Optional WebSocket connections | 100 | 1,000 | 10,000 | custom |
| Webhook endpoints | 3 | 10 | 50 | custom |

Plan allowances and operation-unit metering are on the [pricing page](./pricing.md).
