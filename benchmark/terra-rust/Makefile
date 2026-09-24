.PHONY: lock check build build-guest fmt format

MUSL_CC ?= x86_64-unknown-linux-musl-gcc

lock:
	nice -n 19 ionice -c 3 cargo generate-lockfile

check:
	nice -n 19 ionice -c 3 cargo check --locked

build:
	nice -n 19 ionice -c 3 cargo build --release --locked

build-guest:
	CC_x86_64_unknown_linux_musl=$(MUSL_CC) CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER=$(MUSL_CC) nice -n 19 ionice -c 3 cargo build --release --locked --target x86_64-unknown-linux-musl

fmt:
	cargo fmt --all --check

format:
	cargo fmt --all
