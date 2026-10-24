---
---
<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

<!-- GENERATED from data/canonical/pricing.json (https://terrascale.tech/pricing.json) and data/policies.json by scripts/generate-pricing.mjs — do not edit. -->
# Limits

## Documents API

The current project-scoped contract has a 4 MiB request-body limit, a 1 MiB
document limit, a query page limit of 1–1000 items (default 50), at most 4 sort
keys and 64 projection paths. A query page also stops at 8 MiB of document JSON
or its scan budget, so an empty page can still have a continuation cursor.
Follow cursors until exhaustion; see [queries and pagination](/zh-cn/guides/queries).
The [change stream](/zh-cn/guides/realtime) keeps events for 7 days.

## Plan entitlements

TerraBase enforces these entitlements per organization, shared by all its projects, databases and API keys. Free maps to the hobby engine tier; other plans map to the tier of the same name. Requests beyond the request-rate or concurrency entitlement receive HTTP 429 with Retry-After. The storage limit is a hard ceiling, distinct from the included storage allowance on the pricing page. "No plan ceiling" means the plan sets none; every plan still observes document, page, batch and process safety bounds. Support values are response targets, not an SLA.

| Entitlement (per organization) | Free | Starter | Pro | Team | Enterprise |
| --- | --- | --- | --- | --- | --- |
| Requests/s (burst) | 50 (100) | 200 (400) | 500 (1,000) | 500 (1,000) | No plan ceiling |
| Concurrent HTTP requests | 4 | 8 | 16 | 16 | No plan ceiling |
| Query execution time | 1 s | 2 s | 5 s | 5 s | No plan ceiling |
| Storage limit | 100 MB | 5 GB | 100 GB | 100 GB | No plan ceiling |
| Point-in-time recovery window | 1 day | 3 days | 7 days | 14 days | 30 days |
| Backup retention | 1 day | 3 days | 7 days | 14 days | 30 days |
| Regions per database | 3 | 3 | 3 | 3 | 3 |
| Support response target | Community | 2 business days | 1 business day | 8 hours | Custom |
| Databases | 1 | 5 | 25 | 100 | Custom |

Subscription prices, allowances and metering are on the [pricing page](./pricing.md).

</div>
