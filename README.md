# TerraScale Go SDK (`terrascale.tech/sdk`)

Go SDK with the D-13 core surface: document CRUD, typed schema builder,
typed SQL builder + raw parameterized SQL, subscribe/execute with
checkpoints and typed resets, region pinning, tenants, and management.

## Contract source

`CONTRACT_VERSION` is the committed terrabase contract pin. `make contracts`
requires `CONTRACT_URL`, an immutable versioned package URL, by default and in
CI. Before E-1 publication, development may explicitly use
`CONTRACT_SOURCE=../terrabase/contracts`; this is never an implicit fallback.
The generator verifies the producer manifest and every artifact digest, then
generates operation, route, model, and MessagePack frame metadata in
`contract.go`. The producer's HTTP, reactive, and MessagePack conformance
corpus is required as part of the same bundle; if it is absent, regeneration
fails with the E-1 publication requirement.

## Verify

```sh
make check
```
