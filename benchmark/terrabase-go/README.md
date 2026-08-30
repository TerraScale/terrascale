# terrabase-go

The official Go client for the [TerraBase](https://terrascale.tech)
engine. It speaks **TBEP**, the TerraBase engine protocol, over gRPC
against the tonic server of `crates/terrabase-tbep`.

Per the repository's `AGENTS.md`, **this client is the only client
allowed to communicate with TerraBase.** Any Go service that talks to
the engine does it through this module; nothing else opens a channel
to the engine.

- Module: `terrascale.tech/terrabase-go`
- Go: 1.27
- Dependencies: `google.golang.org/grpc` and
  `google.golang.org/protobuf` only, plus the standard library
- Contract of record: `crates/terrabase-tbep/proto/terrabase/v1/engine.proto`

## Install

```sh
go get terrascale.tech/terrabase-go
```

## Quickstart

```go
import (
    "context"
    "log"
    "os"

    terrabase "terrascale.tech/terrabase-go"
)

func main() {
    root, _ := os.ReadFile("client-root.pem")     // operator trust bundle
    cert, _ := os.ReadFile("client-cert.pem")     // client identity
    key, _ := os.ReadFile("client-key.pem")

    client, err := terrabase.Dial("engine.terrabase.internal:443",
        terrabase.WithRootCerts(root),
        terrabase.WithClientCert(cert, key),
    )
    if err != nil {
        log.Fatal(err)
    }
    defer client.Close()

    ctx := context.Background()

    // Bind the authenticated gateway and exact database every later RPC carries.
    if _, err := client.Bind(ctx, terrabase.BindingScope{
        Principal: "spiffe://terrascale.internal/gateway/database-api",
        Tenant:    "tenant-a",
        Project:   "project-a",
        Database:  "database-a",
    }); err != nil {
        log.Fatal(err)
    }

    // Write one document (create only).
    outcome, err := client.DocumentWrite(ctx,
        terrabase.DocumentPut{
            Collection: "books",
            ID:         "ddia",
            Body: terrabase.Value(map[string]terrabase.Value{
                "title": "Designing Data-Intensive Applications",
                "pages": 616,
            }),
        },
        terrabase.ExpectAbsent(),
        terrabase.AckRegionalQuorum,
        "op-write-42", // also the idempotency key of the first attempt
    )
    if err != nil {
        log.Fatal(err)
    }
    if receipt, ok := outcome.Committed(); ok {
        log.Printf("committed: sequence=%d version=%d", receipt.Sequence, receipt.Version)
    }

    // Read it back.
    document, found, err := client.DocumentRead(ctx, "books", "ddia", terrabase.ReadLevelRegionalQuorum)
    if err != nil || !found {
        log.Fatalf("read: found=%v err=%v", found, err)
    }

    // Query with TQL.
    result, err := client.QueryExecute(ctx, 1, "from books", 100, terrabase.ReadLevelDefault)
    if err != nil {
        log.Fatal(err)
    }
    log.Printf("matched %d documents", result.Count)

    // Subscribe to backbone changes.
    subscription, err := client.Subscribe(ctx, 1, []string{"books"}, terrabase.FromLive())
    if err != nil {
        log.Fatal(err)
    }
    defer subscription.Close()
    for {
        event, err := subscription.Recv()
        if err != nil {
            break // stream ended: close frame, shutdown, or teardown
        }
        if batch, ok := event.BatchEvent(); ok {
            for _, change := range batch.Events {
                _ = change // handle the put/delete
            }
            if _, err := subscription.Acknowledge(ctx, batch.NextSequence-1); err != nil {
                log.Fatal(err)
            }
        }
    }
}
```

See `example_test.go` for the complete annotated walkthrough
(connect, bind, write, read, query, subscribe).

## Connecting

`Dial(target, options...)` is lazy: the first RPC establishes the
connection. The default transport is TLS against the system roots with
no client certificate. Production lanes are mutually authenticated:

| Option | Purpose |
| --- | --- |
| `WithRootCerts(pem)` | trust the operator's root bundle instead of the system roots |
| `WithClientCert(certPEM, keyPEM)` | present the client identity for mTLS |
| `WithInsecure()` | plaintext channel — local and dev lanes only |
| `WithAuthority(name)` | override `:authority` and the TLS server name |
| `WithContextDialer(fn)` | custom connector — Unix-domain-socket lanes, in-memory tests |
| `WithTransportCredentials(c)` | escape hatch for lanes the options above do not cover |
| `WithDefaultReadLevel(level)` | default consistency for reads and queries |

`NewClient(conn)` wraps an existing `grpc.ClientConnInterface`
if you manage the channel yourself.

## Calling

- Call `Bind` first: every other RPC fails closed (locally, and on the
  engine) without the channel identity.
- Every method takes a `context.Context` as its first parameter.
- Mutations carry a retry identity in the `x-tb-idempotency-key`
  metadata. It defaults to the operation ID, then to a fresh random
  key; pass `WithIdempotencyKey(k)` to pin it for safe retries.
- Declared bounds (collection and document lengths, value sizes, list
  bounds, the family vocabulary) are mirrored client-side: malformed
  requests are refused before the wire with `ErrInvalidRequest`.
- Conflict and refusal are **outcomes, not errors** — check
  `outcome.Committed()`, `outcome.Conflict()`, `outcome.Refused()`.

## The error model

A refused RPC fails with a `*terrabase.Error` carrying the engine's
closed error code and its bounded detail, decoded from the gRPC
status details. Match codes with sentinel errors — never string-match
details:

```go
if errors.Is(err, terrabase.ErrOverloaded) {
    // back off and retry
}
typed := new(terrabase.Error)
if errors.As(err, &typed) {
    log.Printf("engine said %s: %s", typed.Code, typed.Detail)
}
```

One sentinel exists per closed engine code: `ErrUnsupportedVersion`,
`ErrMalformedRequest`, `ErrUnknownOperation`, `ErrDigestMismatch`,
`ErrIdempotencyPairing`, `ErrUnboundChannel`, `ErrAlreadyBound`,
`ErrInvalidRequest`, `ErrOperationRefused`, `ErrEngineFailure`,
`ErrSessionRefused`, `ErrSubscriptionRefused`,
`ErrCheckpointRefused`, `ErrShuttingDown`, `ErrRequestTooLarge`,
`ErrOverloaded`, `ErrProtocolVersionMismatch`,
`ErrBackupClusterReadOnly`, `ErrBackupClusterImmutable`,
`ErrColdTierUnavailable`, `ErrFrontierUnreachable`.

Transport-level failures (unavailable, deadline, cancellation) pass
through untouched, so `errors.Is(err, context.DeadlineExceeded)` and
friends keep working. An engine code this client does not know yet
decodes with `Code: CodeUnspecified`.

## Tests

```sh
go test ./...                 # offline suite: fake engine from the generated stubs
go vet -tags integration ./... # compile-check the live lane
TERRABASE_HARNESS=1 go test -tags integration ./...   # live lane (needs cargo)
```

The offline suite runs a fake TBEP server implemented from the
generated service stubs over `bufconn`: complete binding-scope and idempotency
enforcement, engine-shaped failures, document lifecycle, value
round-trips, subscription streaming, local bounds, and a full-surface
smoke test. A second bufconn lane runs a real mutually authenticated
TLS handshake with throwaway certificates.

The integration lane (`integration_test.go`, build tag `integration`)
builds `tbep-production-harness` with
`cargo build -p terrabase-tbep --bin tbep-production-harness --release`,
runs the real Rust engine behind gRPC TLS on loopback, and drives
bind, write, read-back, query, and the typed refusal decode, then
shuts the harness down cleanly. Set `TERRABASE_HARNESS` to the
harness binary path to skip the cargo build. Its limits are stated in
the test's comment: loopback, dev lane, one standalone instance, and
no durability claims.

## Regenerating the bindings

The generated stubs in `terrabase/v1` come from
`crates/terrabase-tbep/proto/terrabase/v1/engine.proto`. Regenerate
only when the proto changes, from the repository root:

```sh
PATH="$HOME/.local/share/mise/installs/go/1.27.0/bin:$PATH" \
nix-shell -p protobuf --run 'protoc --go_out=terrabase-go \
    --go_opt=module=terrascale.tech/terrabase-go \
    --go-grpc_out=terrabase-go \
    --go-grpc_opt=module=terrascale.tech/terrabase-go \
    -I crates/terrabase-tbep/proto \
    crates/terrabase-tbep/proto/terrabase/v1/engine.proto'
```
