---
title: Usage accounting
---

# Usage accounting

TerraScale bills operations and storage. Operations are writes and function invocations. Reads are not billed.

- A committed write counts its changed rows, or each started 4 KiB the database grew, whichever is more. Failed, refused and rolled-back statements count nothing.
- Each function invocation counts one operation, whether the handler succeeds, fails or times out. There is no CPU or duration charge. SQL inside a handler counts like any other SQL.
- Storage is the stored bytes of your databases, sampled every hour and billed per GB-month above the plan allowance.
- SQL reads count toward a monthly fair use and are never billed.
- Shape reads served from the edge are free and never counted.

Writes and invocations share one monthly operations allowance, and every plan has a hard monthly cap on it, so usage cannot run away. See [pricing](/pricing) for each plan's numbers.
