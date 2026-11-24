# Regional API host checks

Each round discovers public regional API origins published by ts-orch via Atlas at
`https://atlas.terrascale.tech/v1/discovery`. Every advertised
origin gets an anonymous `/healthz` check from every status vantage. It must
answer 200 with `status: ok` and its own region ID. No region pin is sent, so
a host silently answered by another region fails this check.

New regions join automatically. A valid catalog retires previously discovered
regions that are absent and reactivates any that return. Retired regions keep
their stored probe history. A discovery outage leaves the last valid catalog in
effect. These checks use no account, API key, database or paid capacity.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
