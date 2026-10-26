# SQL input and shell

`ts sql app - < query.sql` sends one document SELECT; `ts exec app - < script.sql`
sends relational SQL in one backend request/transaction. Neither command splits SQL.

`ts shell app` starts a relational shell with multiline input and session-only history
of submitted statements (Up/Down). Enter `.run` on its own line to submit the entire
buffer, `.reset` to discard it, `.mode document` for the limited read-only collection
SELECT API, or `.mode relational` for SQL tables. `.help` shows help; `.quit` exits.
SQL strings, comments and trigger bodies retain their semicolons. Transactions cannot
span submissions. No history file is written.

Batch input also uses `.run`: `printf 'SELECT 1;\n.run\n' | ts --json shell app`.
JSON emits typed result records as `{"type":"record","data":...}`, then
`{"type":"complete"}` after successful EOF or `.quit`. Prompts and help use stderr.
SQL errors, read failures, interruption, output failures and an unsubmitted buffer
at EOF/`.quit` produce nonzero exit; a failed session never emits `complete`.
EOF is successful only after the pending buffer is submitted or explicitly reset.
