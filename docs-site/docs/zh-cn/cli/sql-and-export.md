---
title: CLI SQL shell
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# CLI SQL shell

`ts shell <db>` is an interactive multiline SQL shell. Type statements across
several lines, then submit with `.run` on its own line. Each `.run` sends the
buffered statements one at a time, each in its own transaction; writes print
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

For one-shot statements, use [`ts sql <db> <statement>`](/zh-cn/cli); for the SQL
model, transactions and limits, see the [SQL overview](/zh-cn/concepts/sql).

</div>
