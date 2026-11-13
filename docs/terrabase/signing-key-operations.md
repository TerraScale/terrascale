# Signing-key recovery

TerraBase validates ts-auth tokens using the last nonempty public key set from its configured issuer. The snapshot
is stored at `<data-directory>/auth-signing-keys.json`, with issuer, version, original successful-fetch time and
last-known-time high-water mark. Writes flush a sibling `.new` file, atomically rename it, then fsync its directory.
Only public material is stored. The data directory is as trusted as the process: restrict its write access accordingly.

A successful nonempty refresh replaces the whole set, including a smaller set; failed, empty or partly unusable sets
keep the previous keys without renewing their fetch time. Entries for another use, of a key type other than RSA
or EC, with an algorithm outside the allow-list, or whose `key_ops` omit `verify` are skipped and never fail a
refresh. A malformed RSA or EC signing entry, a symmetric key or private key material fails it. Accepted entries must be
usable asymmetric public keys, declared `alg` must match the token algorithm, and declared `key_ops` must include
`verify`. The platform issuer publishes P-256 / ES256 / sig keys without `key_ops`.

The allowance is 24 hours of monotonic elapsed time within a process. Stored keys load only after the existing
peer/kernel clock-trust gate opens. UTC before the stored fetch time or high-water mark rejects the entire file.
Remaining allowance is 24 hours minus (high-water mark minus fetch time), further reduced by time since that mark.
Checkpoints are written on fetch, load, and every 60 seconds while running; they advance with monotonic time even
if UTC steps back. A failed write is logged and the process continues. The high-water mark does not decrease.

Across restarts, accounting assumes accurate trusted UTC and durable, unreverted data. A high-water mark detects
rollback below recorded time; it cannot prove downtime if clocks are wrong but agree, prevent restoration of old
state, or recover an unwritten checkpoint. With timely successful writes and scheduling, a crash can lose up to
60 seconds of checkpoint progress; failed or stalled writes and scheduling delays can lose more. The 24-hour monotonic limit is unconditional within a process, while an unconditional real-time cap
across arbitrary wall-clock or data rollback is not guaranteed. No stored keys load without established clock trust.

Unreadable, oversized (over 2 MiB), truncated, malformed, wrong-version, wrong-issuer, empty or unusable stored key
sets are ignored as a whole, logged once without contents and counted; none can prevent the node from starting.
To explicitly drop stored trust, **stop the node, delete the file, then start it**. Deleting it while running does
not clear memory and a checkpoint may recreate it.

Without keys, a token that could be valid gets `503 auth_unavailable` and `Retry-After: 1`. Malformed, expired and
other independently invalid tokens get 401 before keys are requested; unverified claims only reject. Invalid
signatures under held keys also get 401. Readiness remains independent of issuer availability.

Background refresh uses a two-second start interval without keys, otherwise ten minutes; each fetch has one
ten-second deadline. Allow 22 seconds for recovery including a current failed attempt and the next healthy fetch.
With usable keys, unknown IDs share one in-flight fetch and at most two request-triggered fetches per 30-second
cycle: the first and one follow-up for an ID first seen after the last refresh began. The follow-up starts at least 15 seconds after the
first. Allow **25 seconds** for an ID first seen after the latest refresh began under random-ID traffic, with
responsive scheduling and an issuer completing within the ten-second deadline. Tracked IDs are bounded to 1024;
overflow can request only that same follow-up. An ID first observed after that follow-up began can queue the next cycle's first fetch at its
boundary; this adds no fetch to the current cycle. Previously observed IDs have the 30-second admission interval
plus the ten-second deadline (40 seconds). An unsuccessful refresh answers 503, an authoritative absence after
a successful refresh 401. Expiry of held keys brings queued work forward to the no-key background schedule.

A removed key verifies until the next successful refresh, normally ten minutes plus its deadline, or the remaining
snapshot allowance during an outage. Token expiry and API-key revocation admission still apply. An established
WebSocket fails unavailable requests retryably and stays open until its remembered token expiry; idle sockets also
expire. PostgreSQL renewal and pending Execute preserve retryable `57P03`; actual credential refusals use `28P01`.
Known token expiries end PostgreSQL sessions independently of issuer/key availability.

## Metrics and alerts

Scrape the node’s administrative metrics listener. These gauges have no token, project or key-ID labels:

| Prometheus metric | Meaning | Alert |
| --- | --- | --- |
| `terrabase_auth_signing_keys_loaded` | 1 for a usable snapshot, otherwise 0 | Equal to 0 for one minute |
| `terrabase_auth_signing_keys_age_seconds` | Seconds since last successful fetch; -1 before any snapshot loads | Above 1200 for five minutes |
| `terrabase_auth_stored_keys_rejected_total{reason="invalid"}` | Invalid local snapshot ignored (also `clock_backwards`, `expired`) | Investigate local state and clock |
| `terrabase_auth_key_fetches_total{outcome="failure"}` | Failed discovery, JWKS or unusable-key-set fetches | Investigate alongside increasing age |

The age warning leaves more than 23 hours before validation stops. Check discovery and JWKS availability from the
node, the issuer’s storage health, and snapshot persistence warnings. After recovery, verify that loaded is 1 and age
falls below 600 seconds. A `Loaded … stored ts-auth public signing keys` log proves startup used the stored set.
Do not delete the snapshot to clear an alert: that removes restart protection without repairing issuer access.

Project mapping misses are never retained in the mapping cache. Key-status cache refusals require explicit
`key_revoked` or `key_expired` answers after ts-auth’s authoritative read; unavailable reads and generic invalid-token
responses never become cached revocations.
