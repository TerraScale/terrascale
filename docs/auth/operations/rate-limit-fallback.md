# Rate limits fail closed during storage outages

Every fixed window, cooldown, token bucket and reservation uses the persisted TerraBase store.
A failed storage check propagates `503 temporarily_unavailable` and `Retry-After: 2`.
There is no process-local admission budget: restarting or adding an auth instance cannot
mint additional attempts while storage is unavailable. Restoring storage resumes the
same stored budgets, including charges made before the outage.

The API-key status endpoint still bounds signature verification and reads on each process.
Those are local resource admission limits, not substitutes for persisted authentication budgets.
