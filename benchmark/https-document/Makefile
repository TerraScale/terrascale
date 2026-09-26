.DEFAULT_GOAL := help
.PHONY: help lock fmt format check lint build

# Local runs yield to interactive work; CI builds run at full priority.
LOW_PRIORITY := $(if $(filter true,$(CI)),,nice -n 19 ionice -c 3)

help:
	@printf '%s\n' 'lock: create Cargo.lock' 'fmt/format: check or apply source formatting' 'check/lint: compile and lint the driver' 'build: build a release binary'

lock:
	$(LOW_PRIORITY) cargo generate-lockfile

fmt:
	$(LOW_PRIORITY) rustfmt --edition 2024 --check src/main.rs

format:
	$(LOW_PRIORITY) rustfmt --edition 2024 src/main.rs

check:
	$(LOW_PRIORITY) cargo check --locked -j 2

lint:
	$(LOW_PRIORITY) cargo clippy --locked --all-targets -j 2 -- -D warnings

build:
	$(LOW_PRIORITY) cargo build --release --locked -j 2
