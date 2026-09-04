# TerraScale Go SDK (`terrascale.tech/sdk`)

Go SDK with the D-13 core surface: document CRUD, typed schema builder,
typed SQL builder + raw parameterized SQL, subscribe/execute with
checkpoints and typed resets, region pinning, tenants, and management.

## Contract source

`contract.go` pins the source: management routes mirror
`../ts-typescript/sdk-route-manifest.json` (`src/client.ts` path templates);
document/SQL/reactive/tenant paths are a hand-written stub against `/api/v1`
(terrabase `openapi.json` was unreachable at scaffold). Once terrabase
publishes versioned artifacts: pin `CONTRACT_VERSION` and regenerate with
`make contracts` (target to be added alongside the generator).

## Verify

```sh
gofmt -l . && go vet ./... && go build ./... && go test -race ./...
```
