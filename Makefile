.PHONY: check fmt clippy test contracts

check: contracts fmt clippy test

fmt:
	cargo fmt --all --check

clippy:
	cargo clippy --all-targets -- -D warnings

test:
	cargo test

contracts:
	@echo "contracts unavailable: terrabase has not published the pinned contract artifact ($$(cat CONTRACT_VERSION))" >&2
	@echo "refusing to synthesize or fetch an unpublished contract; publish the version first, then regenerate." >&2
	@false
