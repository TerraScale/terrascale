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

## Engine safety tiers

TerraBase enforces these safety tiers per organization, shared by all its projects, databases and API keys. Rate, concurrency and memory refusals return HTTP 429 with Retry-After. The engine tier names Hobby, Pro and Enterprise are implementation safety tiers, not the commercial Free, Starter, Pro, Team and Enterprise plans. No mapping between commercial plans and engine tiers is published. The Enterprise engine tier still observes document, page, batch and process safety bounds.

| Engine limit (per organization) | Hobby | Pro | Enterprise |
| --- | --- | --- | --- |
| Requests/s (burst) | 50 (100) | 500 (1,000) | No plan ceiling |
| Concurrent HTTP requests | 4 | 16 | No plan ceiling |
| Query execution time | 1 s | 5 s | No plan ceiling |
| Candidate rows / JSON scanned per query | 10,000 / 16 MiB | 100,000 / 128 MiB | No plan ceiling |
| Live document JSON storage | 1 GiB | 100 GiB | No plan ceiling |

## Plan policies

The existing engine PITR defaults (days) are hobby 1 / pro 7 / enterprise 30. These engine defaults do not publish PITR entitlements for the commercial plans. No commercial plan-to-engine-tier mapping is published.

Proposed databases per org: Free 1 / Starter 5 / Pro 25 / Team 100 / Enterprise Custom.

Subscription prices, proposed allowances and metering are on the [pricing page](./pricing.md). Usage charges are not yet collected.

</div>
