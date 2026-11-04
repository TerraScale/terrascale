---
title: CLI SQL shell
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

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

For one-shot statements, use [`ts sql <db> <statement>`](/fr/cli); for the SQL
model, transactions and limits, see the [SQL overview](/fr/concepts/sql).

</div>
