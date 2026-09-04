# TerraBase make contracts (Wave-0 foundation, track item E-1; D-47).
#
# `make check` is the repository verification set, exactly what CI runs.
# `make contracts` regenerates the published contract artifacts in place;
# registry publish arrives with the full E-1 publish.
# `make stack` is the local three-member harness (track item E-15).

.PHONY: check contracts stack

check:
	cargo fmt --all --check
	cargo clippy --workspace --all-targets --locked -- -D warnings
	cargo test --workspace --locked
	buf lint
	./scripts/validate-release-contracts.sh
	@if [ -f terrabase-go/go.mod ]; then \
		echo "--- terrabase-go"; \
		test -z "$$(cd terrabase-go && gofmt -l .)"; \
		(cd terrabase-go && go vet ./... && go test -race ./...); \
	fi

contracts:
	mkdir -p contracts/protos/terrabase/v1
	cargo run --locked -p terrabase-server --bin terrabase-openapi -- contracts/openapi.json
	cp crates/terrabase-tbep/proto/terrabase/v1/engine.proto contracts/protos/terrabase/v1/engine.proto
	cp crates/terrabase-tbep/proto/terrabase/v1/family.proto contracts/protos/terrabase/v1/family.proto

stack:
	@echo "NOT-IMPLEMENTED: the local three-member harness is track item E-15 (after E-3, E-5, E-6); this target fails closed until it lands." >&2
	@exit 1
