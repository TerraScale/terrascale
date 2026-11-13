# Apply failure retention

Native I/O and corruption failures have a distinct storage failure case. Apply retries them instead of recording a constraint revert. Generic engine errors and oversized-value errors also remain retryable. The SQL-error revert classifier accepts only constraint and schema failures.

Incoming durable pushes share one queue per database across origins. The current message stays at the head until apply and its acknowledgement succeed. Failures count toward `terrabase.push.apply_retries` and produce a warning. Malformed or unreadable messages, unavailable catalog records, placement changes and loss of regional write authority leave messages unacknowledged in their original durable stream. They block later queued messages for that database until the cause is resolved.

Partner-held transactions that cannot be decrypted or decoded remain in the hold store. Recovery stops later held transactions of that database during the current pass. Each unreadable attempt increments `terrabase.localack.unreadable` with the bounded reason `decrypt` or `decode` and emits an error log identifying the database and sequence.
