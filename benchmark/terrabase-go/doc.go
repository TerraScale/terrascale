// Package terrabasego is the Go client for TerraBase's private TBEP
// control protocol (placement, health, drain, admin, dev-only faults).
//
// It is the only network client allowed to speak TBEP (AGENTS.md). The
// protoc-generated bindings (`buf generate` from
// crates/terrabase-tbep/proto, per buf.gen.yaml) land here with the full
// E-1 contract publish; until then this stub keeps the module and its
// verification gate (`gofmt -l . && go vet ./... && go test -race ./...`)
// alive with no wire surface.
package terrabasego
