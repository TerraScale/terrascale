# Shape CDN delivery

Production shape ciphertext is served at
`https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/<handle>/<object-hash>`.
SDKs fetch these stable URLs without credentials, cookies or query parameters.
The URLs do not require signing. The manifest's `expires_at` governs manifest
refresh; it does not expire the ciphertext URL.

[Shape reads](shape-reads.md) and [read grants](shape-read-grants.md) remain the
protocol and authorization contracts. TerraBase checks the current token, table
grant, database and lineage before releasing a manifest or epoch key. Those
responses remain `private, no-store`. Public TBS1 ciphertext does not grant
access to plaintext. A cached ciphertext GET may succeed for a caller who cannot
obtain its decryption key; unauthorized manifests and keys must still fail.

## Cloudflare policy

On 2026-11-05, Browser Integrity Check rejected Python's HTTP stack with HTTP
403 and `error code: 1010`, including from all three production regions. Changing
only the User-Agent to curl's returned 200 for the same object. This heuristic
also applies to legitimate SDK clients; callers must not impersonate browsers.

Zone configuration ruleset `ca1fd87761ab4266a19c23e1de815891`, version 4, adds
rule `c9030ed0db5e42c8a205ac31bc57a3cf` (`terrabase-shapes-sdk-bic`):

```json
{
  "action": "set_config",
  "action_parameters": { "bic": false },
  "expression": "(http.host eq \"shapes-ts-terrabase-shapes-prod.terrascale.tech\" and starts_with(http.request.uri.path, \"/v1/shapes/\") and not http.request.uri.path contains \"..\" and not raw.http.request.uri.path contains \"..\" and not raw.http.request.uri.path contains \"%\" and http.request.uri.query eq \"\" and http.request.method in {\"GET\" \"HEAD\"})"
}
```

The exception applies to every User-Agent, including an absent header. Keep it
scoped to the production shape host and GET/HEAD object paths. Zone-wide Browser
Integrity Check stays on. Existing configuration rules, managed protections,
custom WAF guard, R2 domain, bucket policy and cache rules remain in place.
The WAF still denies query strings, encoded/traversal paths, other prefixes
(including private `v1/keys/` custody objects) and other methods. No Worker,
client-side workaround, new permission, credential or capacity is required.

The cache rule admits extensionless object names and respects the object's
`public, max-age=31536000, immutable`. Errors are not cached. The standard cache
key retains Origin variants; a tiered HIT on a region's first request is valid.
Use a new object or a fresh Origin variant when proving MISS then HIT in each
colo. Hash and size must match the authorized manifest in either case.

## Inspection and qualification

Use `cf` with the existing workspace authentication; never print tokens:

```sh
cf rulesets get ca1fd87761ab4266a19c23e1de815891 --zone terrascale.tech
cf rulesets get e63dd3e752a547f48bbb0ee49b76767c --zone terrascale.tech
cf rulesets get c2e48e2abf5b4dca89cb6d5009a3839d --zone terrascale.tech
cf zones settings get browser_check --zone terrascale.tech
cf r2 buckets domains custom list --bucket-name ts-terrabase-shapes-prod
```

For a change, fetch the current ruleset, preserve unrelated rules, then use
`cf rulesets update ... --body '<json>'`. Qualify a fresh shape from a Free
throwaway account, each SDK's actual User-Agent or default HTTP-stack header
(including no header), and egress from gru/sjc/fra. Record HTTP status,
`CF-Cache-Status`, `CF-Ray`, bytes and ciphertext SHA-256. Also check granted
manifest/key success, anonymous rejection, table-scope and absent-grant rejection,
and the WAF guard. Delete the proof database and account afterward.

[The live qualification](../../benchmark/bench/2026-11-05-shape-cdn.md) includes
all five SDK User-Agents/defaults, MISS/HIT pairs at GRU/SJC/FRA and authorization
negative controls. F11 owns the separate post-delete eviction and erasure proof.
