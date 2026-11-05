---
title: CLI SQL shell
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# CLI SQL shell

`ts shell <db>` is an interactive multiline SQL shell. Type one statement across
several lines, then submit with `.run` on its own line. Each `.run` sends the
buffer as one SQL statement in its own transaction; an engine call runs exactly
one statement, so give each further statement its own `.run` and use the SDK's
Batch API for atomic multi-statement work. Writes print
`N rows affected` and reads print each row as a JSON line. Use `.reset` to
discard the buffer, `.quit` to leave. Statement history is session-only.

```sh
ts shell example
```

```sql
CREATE TABLE people (id TEXT PRIMARY KEY, name TEXT);
.run
INSERT INTO people (id, name) VALUES ('a1', 'ada');
.run
SELECT id, name FROM people ORDER BY id;
.run
```

For one-shot statements, use [`ts sql <db> <statement>`](/ja/cli); for the SQL
model, transactions and limits, see the [SQL overview](/ja/concepts/sql).

</div>
