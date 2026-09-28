.DEFAULT_GOAL := help
.PHONY: help lock fmt check lint build build-guest

MUSL_CC ?= x86_64-unknown-linux-musl-gcc

help:
	@printf '%s\n' 'lock: explicitly create or refresh this standalone driver lockfile' 'fmt/check/lint/build: source validation and host release build' 'build-guest: static musl release binary for the pinned benchmark guest'

lock:
	cargo generate-lockfile

fmt:
	rustfmt --edition 2024 --check build.rs src/main.rs

check:
	cargo check --locked -j 2

lint:
	cargo clippy --locked --all-targets -j 2 -- -D warnings

build:
	cargo build --release --locked -j 2

build-guest:
	CC_x86_64_unknown_linux_musl=$(MUSL_CC) CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER=$(MUSL_CC) cargo build --release --locked --target x86_64-unknown-linux-musl -j 2
