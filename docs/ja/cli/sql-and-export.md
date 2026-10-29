---
title: CLI SQL shell
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# CLI SQL shell

`ts shell example` starts an interactive SQL session. Enter multiline SQL, then `.run` on its own line to submit the **entire buffer**. Semicolons in strings, comments and trigger bodies are preserved; the shell does not split them. Each submission is one request and transaction; transactions do not span submissions.

`.reset` discards pending SQL; `.help` shows commands; `.quit` exits. EOF or `.quit` with an unsubmitted buffer fails. Up/Down recalls up to 500 submitted statements in memory; history is never written to disk.

```sh
ts exec example - < query.sql
printf 'SELECT 1 AS n;\n.run\n.quit\n' | ts --json shell example
```

`exec` executes one statement. It accepts `-` for stdin. See [SQL](/ja/concepts/sql) for the query interface.

Shell JSON emits one result record per successful `.run`, then the [complete/error terminal](./automation). Prompts, help and diagnostics go to stderr. Any SQL failure makes the final exit nonzero even if later statements succeed; interruption or pending input cannot produce complete.

For recovery, use [backups](/ja/guides/backups-and-restore).

</div>
