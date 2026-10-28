# Go API benchmark client (TS-832)

`api-bench-go` is the Go client of the TS-832 API benchmark. The scenarios and
JSON-lines protocol are in [docs/api-bench.md](../../docs/api-bench.md).

- `api-bench-go startup` builds the SDK client without network access and
  prints `{"type":"startup","init_ms":…}`.
- `api-bench-go run --spec PATH --credentials PATH` creates a `bench-<ulid in lowercase>`
  database, seeds it, answers step lines on stdin and deletes the database when
  stdin closes.
- `make api-bench-build` builds it to `bin/api-bench-go`. The runner starts it;
  run it by hand only against a non-production endpoint.
- SDK: `terrascale.tech/sdk` is pinned in `go.mod` to public ts-go
  `07ffb51e1d66fb2f7a42673683e731bdfc26d158` through a replace to
  `github.com/TerraScale/ts-go`. This SDK has no retry setting, so `run` keeps the
  shipped retry policy.

The TS-761 capacity client is not implemented here.
See [docs/protocol.md](../../docs/protocol.md) for that protocol.
