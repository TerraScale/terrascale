---
title: CLI SQL shell and document exports
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# CLI SQL shell and document exports

`ts shell example` starts in relational mode. Enter multiline SQL, then `.run` on its own line to submit the **entire buffer**. Semicolons in strings, comments and trigger bodies are preserved; the shell does not split them. Each relational submission is one request/transaction; transactions do not span submissions.

`.reset` discards pending SQL; `.mode document` selects the read-only collection SELECT subset and `.mode relational` returns to tables. Reset or submit before switching modes. `.help` shows commands; `.quit` exits. EOF or `.quit` with an unsubmitted buffer fails. Up/Down recalls up to 500 submitted statements in memory; history is never written to disk.

```sh
ts exec example - < query.sql
printf 'SELECT 1 AS n;
.run
.quit
' | ts --json shell example
```

`exec` executes one relational statement; `sql` queries the limited read-only document SQL subset. Both accept `-` for stdin. JSON document SQL includes `matched` and `truncated`; relational JSON preserves positional rows and columns, including duplicate column names. See [SQL concepts](../concepts/sql).

Shell JSON emits one typed result record per successful `.run`, then the [complete/error terminal](./automation). Prompts/help/diagnostics go to stderr. Any SQL failure makes the final exit nonzero even if later statements succeed; interruption or pending input cannot produce complete.

`ts export example` writes raw `{collection,id,document}` NDJSON for **live documents only**, compatible with server NDJSON import. `ts --json export example` wraps those records and adds the common terminal contract; those envelopes are not import input.
Exports exclude relational tables, empty collections, schemas, indexes, TTL, functions and other metadata. One request reads one document snapshot; separate requests/resumptions read new snapshots. Use [backups](../guides/backups-and-restore) for recovery, not a document export as a full database backup.

Write to a new private partial file and publish it only after exit 0; preserve existing backups and discard partial exports on failure. For JSON, also require a complete terminal record. Output failure never guarantees that an earlier import or SQL write was rolled back.

See [access and consent](./access) before importing into a new database.

</div>
