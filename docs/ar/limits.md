---
---
<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

<!-- GENERATED from data/canonical/pricing.json (https://terrascale.tech/pricing.json) and data/policies.json by scripts/generate-pricing.mjs — do not edit. -->
# Limits

## Documents API

The current project-scoped contract has a 4 MiB request-body limit, a 1 MiB
document limit, a query page limit of 1–1000 items (default 50), at most 4 sort
keys and 64 projection paths. A query page also stops at 8 MiB of document JSON
or its scan budget, so an empty page can still have a continuation cursor.
Follow cursors until exhaustion; see [queries and pagination](/ar/guides/queries).
The [change stream](/ar/guides/realtime) keeps events for 7 days.

## Per-organization limits

TerraBase enforces these per organization, shared by all of its projects, databases and API keys. Rate, concurrency and memory refusals return HTTP 429 with Retry-After. Enterprise has no plan ceiling but still observes document, page, batch and process safety bounds. TerraBase has no Starter tier yet.

| Limit (per organization) | Hobby | Pro | Enterprise |
| --- | --- | --- | --- |
| Requests/s (burst) | 50 (100) | 500 (1,000) | No plan ceiling |
| Concurrent HTTP requests | 4 | 16 | No plan ceiling |
| Query execution time | 1 s | 5 s | No plan ceiling |
| Candidate rows / JSON scanned per query | 10,000 / 16 MiB | 100,000 / 128 MiB | No plan ceiling |
| Live document JSON storage | 1 GiB | 100 GiB | No plan ceiling |

## Plan policies

PITR retention (days): Hobby 1 / Pro 7 / Enterprise 30.

Databases per org: Hobby 1 / Starter 5 / Pro 25 / Enterprise Custom.

Plan allowances and operation-unit metering are on the [pricing page](./pricing.md).

</div>
