.DEFAULT_GOAL := help
.PHONY: help lock fmt format check lint build test

help:
	@printf '%s\n' 'lock: create Cargo.lock' 'fmt/format: check or apply source formatting' 'check/lint: compile and lint the driver' 'build: build a release binary'

lock:
	cargo generate-lockfile

fmt:
	rustfmt --edition 2024 --check src/main.rs

format:
	rustfmt --edition 2024 src/main.rs

check:
	cargo check --locked -j 2

lint:
	cargo clippy --locked --all-targets -j 2 -- -D warnings

build:
	cargo build --release --locked -j 2

test:
	cargo test --locked -j 2
	python3 -m unittest discover -s . -p 'test_*.py'
