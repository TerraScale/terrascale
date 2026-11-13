# Apply failure retention

Native I/O and corruption failures have a distinct storage failure case. Apply retries storage failures, busy, interrupted, read-only, stale-row and unmatched engine errors without recording a revert. Engine syntax, missing-table and missing-column errors also retry because apply generates its own SQL. ApplyRun rejects invalid changes before execution.

Constraint failures, known SQL-error phrases (including datatype mismatch and integer overflow), and oversized-value errors revert. These permanent outcomes do not block later transactions. Already-existing-object errors remain outside the rejection classifier.

Partner-held transactions that cannot be decrypted or decoded remain in the hold store. Recovery stops later held transactions of that database during the current pass. Each unreadable attempt increments `terrabase.localack.unreadable` with the bounded reason `decrypt` or `decode`; the first unreadable attempt for a hold logs an error during the process lifetime. Log tracking is removed when the hold is dropped or released. Holds whose database is absent from the catalog are dropped and counted by `terrabase.localack.deleted`. An unavailable catalog retains the hold.
