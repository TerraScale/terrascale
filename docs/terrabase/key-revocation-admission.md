# API key admission: new work starts only on a yes it can outlast

Amends [key-revocation.md](key-revocation.md) (#853). The revocation bounds are unchanged. A revoked or expired key is
refused within 2 seconds, and work already running on it is cut off within 2.5 seconds.

## The defect

A yes holds for 2 seconds from when it was asked. Running work is aborted when no new yes arrives before the last one
runs out, and its watch asks again once the yes is 1 second old. A request was admitted on any unexpired yes, so a slow
answer (ts-auth took 1.8 seconds on a loaded CI runner) admitted a write with 0.2 seconds left. The watch could not renew
it in time, and the connection was reset mid-request: an RST, with the write's outcome unknown to the client. The
contract's answer for a slow ts-auth is `503 auth_unavailable` with `Retry-After: 1`, before any work starts.

## Admission

- An HTTP request on a key's token starts only on a yes with at least **1 second** left: the time its watch needs to ask
  again (at 1 second of age) and get the new answer before the yes runs out.
- A yes older than 1 second waits for the question already in flight, or asks one. If even the new answer leaves less
  than 1 second (ts-auth took over a second to answer), the request is refused with `503 auth_unavailable` and
  `Retry-After: 1`. Nothing ran, so a client retry cannot repeat a write.
- So that a busy key's requests still do not wait, a request asks again in the background once the yes is half a second
  old. A busy key costs about two ts-auth calls per node per second, up from one. That is still far below ts-auth's
  20 storage-backed checks per second per key, string and project.
- Watches of running work, Postgres statements and the refusal rules are unchanged.

## ts-auth answers in one storage round trip

`GET /v1/terrascale/api-keys/self` reads the key, the organization its token names (`org_id`) and the project
(`project_id`) together. Before, it read the key first and the organization and project after it. The answer is the same:
the key must still belong to the token's organization, or it answers `key_revoked`.
