.PHONY: check contracts stack stack-check stack-down

stack:
	./scripts/stack.sh up

stack-check:
	./scripts/stack.sh check

stack-down:
	./scripts/stack.sh down

check:
	cargo fmt --all --check
	cargo clippy --workspace --all-targets --locked -- -D warnings
	cargo test --workspace --locked
	buf lint
	@if [ -n "$(CONTRACT_PREVIOUS)" ]; then \
		./scripts/validate-release-contracts.sh --version "$(CONTRACT_VERSION)" --previous "$(CONTRACT_PREVIOUS)"; \
	else \
		./scripts/validate-release-contracts.sh --version "$(CONTRACT_VERSION)"; \
	fi
	@if [ -f terrabase-go/go.mod ]; then \
		echo "--- terrabase-go"; \
		test -z "$$(cd terrabase-go && gofmt -l .)"; \
		(cd terrabase-go && go vet ./... && go test -race ./...); \
	fi

CONTRACT_VERSION ?= v1
CONTRACT_PREVIOUS ?=

contracts:
	cargo run --quiet --locked -p terrabase-server --bin terrabase-contracts -- \
		--repository-root . \
		--output contracts \
		--version "$(CONTRACT_VERSION)"
	@if [ -n "$(CONTRACT_PREVIOUS)" ]; then \
		./scripts/validate-release-contracts.sh --version "$(CONTRACT_VERSION)" --previous "$(CONTRACT_PREVIOUS)"; \
	else \
		./scripts/validate-release-contracts.sh --version "$(CONTRACT_VERSION)"; \
	fi
