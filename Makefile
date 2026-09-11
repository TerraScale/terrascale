.PHONY: check fmt clippy test contracts

CONTRACT_VERSION ?= v1
CONTRACT_SOURCE ?=
CONTRACT_URL ?=
CONTRACT_ARCHIVE_SHA256 ?= $(shell tr -d '[:space:]' < CONTRACT_ARCHIVE_SHA256)

check: contracts fmt clippy test

fmt:
	cargo fmt --all --check

clippy:
	cargo clippy --all-targets -- -D warnings

test:
	cargo test

contracts:
	@CONTRACT_VERSION="$(CONTRACT_VERSION)" CONTRACT_SOURCE="$(CONTRACT_SOURCE)" CONTRACT_URL="$(CONTRACT_URL)" CONTRACT_ARCHIVE_SHA256="$(CONTRACT_ARCHIVE_SHA256)" \
		python3 scripts/contracts.py --version "$(CONTRACT_VERSION)"
	rustfmt --edition 2021 src/generated_contract.rs
