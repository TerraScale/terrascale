# X-5 reader ramp

This tool is the production-gate load generator and result validator for the
reactive CDN path. It drives one configured log URL through 10,000, 100,000,
and 1,000,000 concurrent readers while the configured origin produces exactly
16 writes/s. It records client latency and consumes a normalized telemetry
adapter for Cloudflare edge misses, upper-tier misses, origin fetches, cache
bytes, `sync.api.` Worker requests (the expected production value is zero), and origin-fill cost per reader-hour. It also calls a credentialed fault
adapter for `cache-bypass`, `eviction`, `rule-change`, and `capability-expiry`.

The command refuses to make requests without `--confirm-live`, a capability,
and a mixed (unauthorized) capability. HTTPS runs also require telemetry and
injection URLs plus their bearer credentials. HTTP is accepted only for a
loopback stub with `--allow-http`. No absent counter is replaced with zero.

Run a real measurement only with credentials supplied outside source control:

```sh
cd ts-atlas/reader-ramp
go run . run \
  --endpoint 'https://sync.api.example/api/v1/reactive/views/V/1/log?offset=0' \
  --capability "$READER_RAMP_CAPABILITY" \
  --mixed-capability "$READER_RAMP_MIXED_CAPABILITY" \
  --telemetry-url "$READER_RAMP_TELEMETRY_URL" \
  --telemetry-token "$READER_RAMP_TELEMETRY_TOKEN" \
  --injection-url "$READER_RAMP_INJECTION_URL" \
  --injection-token "$READER_RAMP_INJECTION_TOKEN" \
  --confirm-live --output result.json
go run . validate --result result.json
```

The telemetry URL is an operator-owned adapter, not a fabricated fixture. It
must return cumulative JSON counters, sampled at the runner's `at` query
parameter:

```json
{"edge_misses":0,"upper_tier_misses":0,"origin_fetches":0,
 "total_bytes":0,"cache_hit_bytes":0,"worker_requests":0,
 "origin_fill_cost_usd":0}
```

The injection URL accepts `POST {"scenario":"...","endpoint":"..."}` and
must return 2xx only after the named transition is active. The validator
rejects stub mode by default. `--allow-stub` is useful for checking local
reader and MessagePack behavior, but it cannot prove Cloudflare metrics or
the zero-Worker production claim.

Build and test without changing the ts-atlas package manifest:

```sh
go test ./...
go vet ./...
```
