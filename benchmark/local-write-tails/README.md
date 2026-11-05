# Concurrent local-write tails

Run through `ts-heavy-any` on **beta**, using the public Python SDK installed
from the selected monorepo revision (`uv run --with ./sdk/python`). Create a
unique account with `ts-throwaway-account TAG`, then run:

```
python benchmark/local-write-tails/run.py --tag TAG --output /private/results.jsonl
```

The harness creates a GRU-home all-regions database and the #1105 table, then
runs three regional workers concurrently from beta. Each worker writes 100
fresh ULIDs per repeat, for three repeats, with SELECTs in the other two
regions after each write. Automatic SDK retries are disabled as in acceptance.
Every sample, response ID, route and server timing is retained. Read-only
warmup waits for local replica reads. No insert warms the measured writer.
Thirty reused-connection health requests measure each endpoint's beta baseline;
the initial connection sample is retained separately. Read-only warmup follows
baseline measurement so the first insert does not artificially start after an
idle authentication-expiry gap. This does not change token validity.

The harness also retains ten home-durable and ten forced-home samples per
region and final inventories. These modes are separate from default local ack.
A failed write or missing row remains in the evidence; it is not retried by the
harness. The SDK's existing protocol-level resend behavior remains unchanged.

Use the existing peer snapshot collector before and after to verify source
stability and collect background key-refresh metrics. Snapshot collection is
read-only; load runs only on beta. Remove the database/account afterward using
`ts-throwaway-account --delete TAG`, completing a pending deletion if required.
The harness leaves resources present on failure so their IDs remain reviewable;
the account owner must always finish cleanup. Credentials remain in private
helper files, or in a 0600 JSON file provided with `--config`. `--existing`
allows diagnosis against a previously created database.
