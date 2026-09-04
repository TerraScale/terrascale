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

CONTRACT_VERSION ?= v1

contracts:
	cargo run --quiet --locked -p terrabase-server --bin terrabase-contracts -- \
		--repository-root . \
		--output contracts \
		--version "$(CONTRACT_VERSION)"

stack:
	@echo "NOT-IMPLEMENTED: the local three-member harness is track item E-15 (after E-3, E-5, E-6); this target fails closed until it lands." >&2
	@exit 1
