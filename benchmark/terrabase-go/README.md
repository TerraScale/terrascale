# `terrabase-go`

The Go client for TerraBase's private TBEP control protocol. It is the only
network client allowed to speak TBEP.

Regenerate the bindings after editing the proto (CI pins `buf v1.50.0`,
`protoc v34.1`, `protoc-gen-go v1.36.12`, `protoc-gen-go-grpc v1.6.2`):

```bash
PATH="$(go env GOPATH)/bin:$(go env GOBIN):$PATH" buf generate
```

The committed stubs are drift-checked by `scripts/validate-release-contracts.sh`.
The full generated tree lands with the E-1 contract publish; until then this
directory holds only the module stub and its pin test.
