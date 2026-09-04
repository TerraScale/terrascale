.PHONY: check fmt clippy test contracts

check: fmt clippy test

fmt:
	cargo fmt --all --check

clippy:
	cargo clippy --all-targets -- -D warnings

test:
	cargo test

contracts:
	@echo "pinned contract: $$(cat CONTRACT_VERSION)"
	@echo "terrabase contract artifacts not yet published; run 'make contracts' after pinning CONTRACT_VERSION."
