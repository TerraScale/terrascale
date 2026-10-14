# 21. Slice plan: Replication outage window

**Replication outage window.** Between R2a and R10a, main does not replicate across machines. This is acceptable:
the Fly apps hold no customer data yet and nothing replicates on Fly today (no peer configuration), but it must be a
known state. The e2e job marks E1 to E7 expected-failure in that window.
