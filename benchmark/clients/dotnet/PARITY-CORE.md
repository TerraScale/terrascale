# Authenticated parity observation foundation

`ParityAttempts` observes sends beneath the shipped SDK without changing retry,
auth, pin or protocol behavior. It validates the exact read target, a single
request pin, a single matching serving-region header, status and negotiated
HTTP/1.1 or HTTP/2. Raw controls can require an exact protocol; SDK observations
accept either and retain their counts. Snapshots are taken only after all reads
drain, and deltas compare transport attempts to logical starts.

The offline controls exercise the pinned SDK's actual 503 retry and lack of
automatic 401 retry, plus invalid metadata and concurrent requests. They do not
negotiate TLS or test ALPN. SocketsHttpHandler internal wire replays are invisible
to DelegatingHandler; no zero-wire-retry claim is supported.

`ParityPeers` discovers all regions from `TS_PEERS_FILE`, matching the Go
runner's strict node/region/IPv4 rows. It sorts and deduplicates regions, requires
gru/sjc/fra, and rejects any malformed row without printing its contents.

This is not a complete runner. Full document checks,
token-exchange observation, connection counts, bounded windows/drains and
owned-resource recovery remain necessary before live parity can be qualified.
No latency, throughput, regional result or speedup is established here.
