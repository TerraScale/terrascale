---
---
<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

# A database per customer (draft)

> Draft. Tenant provisioning workflows and capacity claims remain unqualified.

A separate database for each customer can make access scope, usage, recovery,
and residency easier to manage. Each database needs explicit permissions and
an immutable placement policy; a separate database does not replace correct
authorization in the application.

Restore and branching workflows must preserve the source database’s residency
and access restrictions. Tenant and management fixtures are retained in the
[snippet guide](/ja/snippets), but are not runnable until their public contracts
are available. Do not assume a provisioning API is ready from this pattern.

Databases and branches share organization plan allowances. Logical storage,
including retained history, remains metered even when the database is idle.
Hobby databases pause after 30 idle days and may become eligible for deletion
after 90 with advance notices unless deletion protection is enabled. Paid
databases are never idle-reaped. Separate unpaid-storage and required-card
policies still apply; see [pricing](/ja/pricing/).

No database-count capacity or idle-cost claim is established by this draft.
Those claims require workload and cost measurements.
