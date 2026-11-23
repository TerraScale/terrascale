# Regional API host checks

Each round discovers public regional API origins from ts-orch at
`https://api.terrascale.tech/v1/regions/origins`. Every advertised
origin gets an anonymous `/healthz` check from every status vantage. It must
answer 200 with `status: ok` and its own region ID. No region pin is sent, so
a host silently answered by another region fails this check.

New regions join automatically. A discovery outage retains existing checks;
it cannot remove failing hosts from monitoring. These cheap checks use no
account, API key, database or paid capacity.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
