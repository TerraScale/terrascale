.PHONY: check fmt clippy test contracts

CONTRACT_VERSION ?= v1
CONTRACT_URL ?=
# The checked-in bundle is the reproducible default for local verification.
# Published/CI builds can still set CONTRACT_URL (and its digest), while an
# explicit CONTRACT_SOURCE remains available for producer development.
ifeq ($(strip $(CONTRACT_URL)),)
CONTRACT_SOURCE ?= contracts/$(CONTRACT_VERSION)
else
CONTRACT_SOURCE ?=
endif
CONTRACT_ARCHIVE_SHA256 ?= $(shell tr -d '[:space:]' < CONTRACT_ARCHIVE_SHA256)

check: contracts fmt clippy test

fmt:
	cargo fmt --all --check

clippy:
	cargo clippy --locked --all-targets -- -D warnings

test:
	cargo test --locked

contracts:
	@CONTRACT_VERSION="$(CONTRACT_VERSION)" CONTRACT_SOURCE="$(CONTRACT_SOURCE)" CONTRACT_URL="$(CONTRACT_URL)" CONTRACT_ARCHIVE_SHA256="$(CONTRACT_ARCHIVE_SHA256)" \
		python3 scripts/contracts.py --version "$(CONTRACT_VERSION)"
	rustfmt --edition 2021 src/generated_contract.rs
