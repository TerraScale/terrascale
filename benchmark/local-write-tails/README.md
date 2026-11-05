# Concurrent local-write tails

Run through `ts-heavy-any` on **a dev host selected through admission**, using the public Python SDK installed
from the selected monorepo revision (`uv run --with ./sdk/python`). Create a
unique account with `ts-throwaway-account TAG`, then run:

```
python benchmark/local-write-tails/run.py --tag TAG --output /private/results.jsonl
```

The harness creates a GRU-home all-regions database and the #1105 table, then
runs three regional workers concurrently from that host. Each worker writes 100
fresh ULIDs per repeat, for three repeats, with SELECTs in the other two
regions after each write. Automatic SDK retries are disabled as in acceptance.
Every sample, response ID, route and server timing is retained. Read-only
warmup waits for local replica reads. No insert warms the measured writer.
One hundred reused-connection SELECT-1 requests measure each endpoint's client baseline before writes;
the initial connection sample is retained separately. Read-only warmup follows
baseline measurement. A regional barrier keeps every measured write after all three
workers finish their baseline and readiness requests. The source starts writes after the original cross-region
read warmup; no extra source request refreshes authority immediately before its first insert.

The harness also retains ten home-durable and ten forced-home samples per
region and final inventories with exact ID/value comparisons against every acknowledged write. These modes are separate from default local ack.
A failed write or missing row remains in the evidence; it is not retried by the
harness. The SDK's existing protocol-level resend behavior remains unchanged.

Use the existing peer snapshot collector before and after to verify source
stability and collect background key-refresh metrics. Take snapshots through
read-only calls; run load only on dev hosts, never serving nodes. Remove the database/account afterward using
`ts-throwaway-account --delete TAG`, completing a pending deletion if required.
The harness leaves resources present on failure so their IDs remain reviewable;
the account owner must always finish cleanup. Credentials remain in private
helper files, or in a 0600 JSON file provided with `--config`. `--existing`
allows diagnosis against a previously created database.
