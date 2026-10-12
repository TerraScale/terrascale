---
---
<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

<!-- GENERATED from data/canonical/pricing.json (https://terrascale.tech/pricing.json) and data/policies.json by scripts/generate-pricing.mjs — do not edit. -->
# Limits

## Documents API

The current project-scoped contract has a 4 MiB request-body limit, a query
page limit of 1–1000 items (default 50), at most 4 sort keys and 64 projection
paths. A query page also stops at 8 MiB of document JSON or its scan budget,
so an empty page can still have a continuation cursor. Follow cursors until
exhaustion; see [queries and pagination](/fr/guides/queries).

## Plan policies (pre-launch)

The figures below come from the public pricing catalog and documentation
policy. They do not establish deployed enforcement, live-update availability
or qualified retention, recovery and deletion behavior. Confirm the serving
release before depending on them.

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

</div>
