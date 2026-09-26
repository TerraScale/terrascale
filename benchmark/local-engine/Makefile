.DEFAULT_GOAL := help
.PHONY: help lock fmt check lint build build-guest

MUSL_CC ?= x86_64-unknown-linux-musl-gcc
LOW_PRIORITY := nice -n 19 ionice -c 3

help:
	@printf '%s\n' 'lock: explicitly create or refresh this standalone driver lockfile' 'fmt/check/lint/build: source validation and host release build' 'build-guest: static musl release binary for the pinned benchmark guest'

lock:
	$(LOW_PRIORITY) cargo generate-lockfile

fmt:
	$(LOW_PRIORITY) rustfmt --edition 2024 --check build.rs src/main.rs

check:
	$(LOW_PRIORITY) cargo check --locked -j 2

lint:
	$(LOW_PRIORITY) cargo clippy --locked --all-targets -j 2 -- -D warnings

build:
	$(LOW_PRIORITY) cargo build --release --locked -j 2

build-guest:
	CC_x86_64_unknown_linux_musl=$(MUSL_CC) CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER=$(MUSL_CC) $(LOW_PRIORITY) cargo build --release --locked --target x86_64-unknown-linux-musl -j 2
