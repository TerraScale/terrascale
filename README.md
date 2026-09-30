# TerraScale Rust SDK

The SDK speaks TerraBase's native JSON document protocol. Native paths carry UUID tenant, database, collection and document identities and a canonical full-range decimal shard. Numeric field IDs and tagged values preserve missing/null, signed 64-bit integers, bytes, maps and arrays. Point reads retain observed context, tombstones and all concurrent siblings. Native values have at most 16 levels, arrays cannot occur under an ancestor array, and the entire encoded document is capped at 1 MiB.

```rust,no_run
use terrascale::{ClientConfig, DatabaseScope, TerraScaleClient};
# async fn example(native_key: &str) -> terrascale::Result<()> {
let client = TerraScaleClient::new(ClientConfig::builder()
    .api_key(native_key)
    .build()?);
let scope = DatabaseScope::new(
    "00000000-0000-0000-0000-000000000001".parse()?,
    "00000000-0000-0000-0000-000000000002".parse()?,
    u64::MAX,
);
let point = client.documents().get(&scope,
    "00000000-0000-0000-0000-000000000003".parse()?,
    "00000000-0000-0000-0000-000000000004".parse()?,
).await?;
let _fields = point.live_fields()?;
# Ok(())
# }
```

`PreparedTransaction` freezes scope, transaction identity, authority, effect digest and exact request bytes. `Condition::Absent` requires no retained state; a deleted point must use its `state_version`. `Condition::Version` is a producer state token, and observed context alone is not compare-and-set. A caller must retain the prepared operation after `CommitOutcome::Uncertain`, resolve it with `transactions().status()`, or explicitly retry the same frozen operation. Pending, unknown and expired statuses do not prove an abort. Receipt and status validation binds the original identity, effect digest, authority and database/shard event.

`subscriptions().snapshot()` pages a native predicate view and yields a poll cursor. `subscriptions().poll()` returns complete ordered groups; a null effect removes a document from the view. `SubscriptionView` binds tenant/database/shard, collection and predicate, requires the actual request cursor to match its snapshot/poll phase, and validates a complete page before publishing groups and the next cursor. Cursors stay opaque and are bound by the producer to scope and predicate.

The default API base is `https://api.terrascale.tech`, the Atlas reverse proxy.
The SDK first resolves scoped native discovery at
`https://atlas.terrascale.tech/api/v1/discovery`, using the existing canonical
`tbk1` credential and UUID scope contract, then pins direct traffic to the
top-ranked eligible `https://<region>.api.terrascale.tech` endpoint. The pin
lasts for the document's conservative `refreshSeconds`/expiry lifetime.
Transport failures, 5xx, and 421 demote the failed region, fail over safe
requests to the next ranked eligible endpoint, and re-pin later requests.
Unavailable, malformed, or timed-out discovery use the Atlas API proxy, with a
short failure backoff. Local credential, scope, capability, clock, and observation validation
remain enforced. An explicit custom `api_base_url` disables discovery.

`preferred_region` and `region_affinity(RegionAffinity::Failover)` prefer an
eligible region while allowing failover; `RegionAffinity::Strict` requires a
preferred region and uses its `.api.` origin directly. `discovery_base_url`
selects the metadata origin (native credentials require canonical Atlas), and
`discovery_timeout` defaults to three seconds. `RegionRtt` hints supply measured
ranking inputs; Atlas owns ranking and admission. Routing failover does not
consume `max_retry_attempts`; native commits are never automatically replayed.
See [regional routing](docs/regional-routing.md) for routing and fallback details.

Discovery caches at most 128 scopes without evicting their authority/revision floors. Floors survive lease expiry within the client instance; deployment/cell moves and generation/revision regressions fail closed. A lease is at most ten seconds, clamped down by absolute expiry and transport time, and checked with wall and monotonic clocks. The client does not persist floors across process restart; the receiver owns durable floors. Clock failures/regressions fail closed. Runtime acceptance still requires independently measured healthy clock bounds and provenance. Safe native retries refresh expired routing; mutations are never automatically replayed.

Point needs `client_v1 + read`, query additionally needs `query`, and snapshot/poll additionally need `subscriptions`. Transaction status needs `client_v1 + write`, including reconciliation after a capability downshift. Single unconditional commit needs `client_v1 + write`; conditional/multiple commits require additional capabilities which remain false in this v1 Atlas response. Direct admitted native fixture capabilities are unchanged. A caller-supplied single-writer identity is rejected until signed durable admission and commit fencing are qualified; leaderless writes do not establish security authority.

Authentication remains a separate `auth_idp()` client using `auth_base_url` and `auth_api_key`. TerraBase credentials and routing selectors never reach ts-auth. Authentication mutations are never automatically replayed. The canonical issuer is unchanged by regional endpoint selection.

The obsolete HTTP SQL, management, file, schema and reactive shim APIs have been removed. SQL is a required integration capability through TerraBase's actual PostgreSQL `sql_wire`; a PostgreSQL consumer implementation and packaged qualification remain open work. Native unique/schema extensions, actual Atlas/native packaged caller and clock qualification, durable security authority and real packaged producer evidence remain release blockers. Local codec tests are not deployment or serving evidence.

Run `cargo fmt --all --check`, `cargo clippy --locked --all-targets --all-features -- -D warnings`, and `cargo test --locked --all-features`. `scripts/check-native-contract.py` verifies the exact producer contract pin and `scripts/check-atlas-contract.py` separately verifies the receiver contract and fixed-clock codec fixtures migrated to the regional endpoint decision; `scripts/qualification/run.sh` exercises a separately unpacked `.crate` against a real native producer and fails when required fixture configuration is absent.
