# SQL input and shell

`ts sql app - < script.sql` sends one SQL statement in one backend request and
transaction. The command does not split SQL. `--json` returns columns, positional
rows, affected count and last insert ID from the SDK, preserving duplicate column
names and integers, as one JSON object.

`ts shell app` starts a SQL shell with multiline input and session-only history
of submitted statements (Up/Down). Enter `.run` on its own line to submit the entire
buffer as one transaction, `.reset` to discard it. `.help` shows help; `.quit` exits.
SQL strings, comments and trigger bodies retain their semicolons. Transactions cannot
span submissions. No history file is written.

Batch input also uses `.run`: `printf 'SELECT 1;\n.run\n' | ts --json shell app`.
JSON emits typed result records as `{"type":"record","data":...}`, then
`{"type":"complete"}` after successful EOF or `.quit`. Prompts and help use stderr.
SQL errors, read failures, interruption, output failures and an unsubmitted buffer
at EOF/`.quit` produce nonzero exit; a failed session never emits `complete`.
EOF is successful only after the pending buffer is submitted or explicitly reset.
