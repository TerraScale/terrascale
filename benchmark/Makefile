.PHONY: check contracts contract-archive stack stack-check stack-test stack-config-test stack-pre-engine stack-down

stack:
	./scripts/stack.sh up

stack-check:
	./scripts/stack.sh check

stack-test:
	./scripts/stack.sh test

stack-config-test:
	./tests/stack/test_stack_config.sh
	python3 ./tests/stack/test_webhook_receiver.py

# Transitional harness for engine work before E-5/E-6 are available. It is
# deliberately separate: the normal `stack` target never starts the X-4 stub.
stack-pre-engine:
	./scripts/stack.sh pre-engine

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

# Package the producer-owned bundle once, with deterministic tar metadata.
# Consumers receive the archive and its SHA-256 sidecar; they never infer a
# source from a sibling checkout. Existing output is immutable and is only
# accepted again when its bytes and metadata are identical.
CONTRACT_ARCHIVE_DIR ?= contract-archives
CONTRACT_ARCHIVE := $(CONTRACT_ARCHIVE_DIR)/$(CONTRACT_VERSION).tar

contract-archive: contracts
	python3 scripts/package-contract-archive.py \
		--source contracts --version "$(CONTRACT_VERSION)" \
		--output "$(CONTRACT_ARCHIVE)"
