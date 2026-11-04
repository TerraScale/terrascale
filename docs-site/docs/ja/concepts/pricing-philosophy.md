---
title: Usage accounting
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# Usage accounting

TerraScale bills writes and storage. Reads are not billed.

- A committed write counts its changed rows, or each started 4 KiB the database grew, whichever is more. Failed, refused and rolled-back statements count nothing.
- Storage is the stored bytes of your databases, sampled every hour and billed per GB-month above the plan allowance.
- SQL reads count toward a monthly fair use and are never billed.
- Shape reads served from the edge are free and never counted.

Every plan has a hard monthly write cap, so usage cannot run away. See [pricing](/ja/pricing) for each plan's numbers.

</div>
