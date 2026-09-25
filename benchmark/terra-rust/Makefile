.DEFAULT_GOAL := help
.PHONY: help lock check check-engine check-three-way lint lint-engine lint-three-way build build-guest build-guest-engine build-guest-launcher build-guest-three-way test-build test-build-launcher test-vm test fmt format

MUSL_CC ?= x86_64-unknown-linux-musl-gcc
SUBJECT_REPO ?= $(abspath ../../terrabase)
HARNESS_BINARY ?=
GUEST_AGENT_BINARY ?=
HARNESS_SHA256 ?=
GUEST_AGENT_SHA256 ?=

help:
	@printf '%s\n' 'check/lint/build: SQLite-only driver checks and host build' 'check-engine/lint-engine/build-guest-engine: feature-enabled TerraBase adapter' 'build-guest: static musl SQLite-only binary' 'build-guest-launcher/test-build-launcher: static musl P04 analysis launcher and compile-only unit tests' 'test-build: compile feature-enabled musl unit tests without running them' 'test-vm (or test): run TEST_BINARY inside Firecracker; set absolute TEST_BINARY and unique RUN_ID' 'lock: explicitly refresh Cargo.lock; fmt/format: check or apply Rust formatting'

lock:
	nice -n 19 ionice -c 3 cargo generate-lockfile

check:
	nice -n 19 ionice -c 3 cargo check --locked -j 2

check-engine:
	nice -n 19 ionice -c 3 cargo check --locked --features terrabase -j 2

check-three-way:
	nice -n 19 ionice -c 3 cargo check --locked --features three-way --bin p04-three-way -j 2

lint:
	nice -n 19 ionice -c 3 cargo clippy --locked --all-targets -j 2 -- -D warnings

lint-engine:
	nice -n 19 ionice -c 3 cargo clippy --locked --all-targets --features terrabase -j 2 -- -D warnings

lint-three-way:
	nice -n 19 ionice -c 3 cargo clippy --locked --features three-way --bin p04-three-way -j 2 -- -D warnings

build:
	nice -n 19 ionice -c 3 cargo build --release --locked --bin terrabase-sqlite-baseline -j 2

build-guest:
	CC_x86_64_unknown_linux_musl=$(MUSL_CC) CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER=$(MUSL_CC) nice -n 19 ionice -c 3 cargo build --release --locked --bin terrabase-sqlite-baseline --target x86_64-unknown-linux-musl -j 2

build-guest-engine:
	CC_x86_64_unknown_linux_musl=$(MUSL_CC) CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER=$(MUSL_CC) nice -n 19 ionice -c 3 cargo build --release --locked --features terrabase --bin terrabase-sqlite-baseline --target x86_64-unknown-linux-musl -j 2

build-guest-three-way:
	CC_x86_64_unknown_linux_musl=$(MUSL_CC) CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER=$(MUSL_CC) nice -n 19 ionice -c 3 cargo build --release --locked --features three-way --bin p04-three-way --target x86_64-unknown-linux-musl -j 2

build-guest-launcher:
	CC_x86_64_unknown_linux_musl=$(MUSL_CC) CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER=$(MUSL_CC) nice -n 19 ionice -c 3 cargo build --release --locked --bin p04-analysis-launcher --target x86_64-unknown-linux-musl -j 2

test-build:
	@command -v "$(MUSL_CC)" >/dev/null 2>&1 || { echo 'musl C compiler is required' >&2; exit 2; }
	CC_x86_64_unknown_linux_musl=$(MUSL_CC) CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER=$(MUSL_CC) nice -n 19 ionice -c 3 cargo test --locked --no-run --bin terrabase-sqlite-baseline --features terrabase --target x86_64-unknown-linux-musl -j 2
	@printf '%s\n' 'Compiled only. Resolve Cargo’s printed test executable path to an absolute path for make test-vm TEST_BINARY=... RUN_ID=...'

test-build-launcher:
	@command -v "$(MUSL_CC)" >/dev/null 2>&1 || { echo 'musl C compiler is required' >&2; exit 2; }
	CC_x86_64_unknown_linux_musl=$(MUSL_CC) CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER=$(MUSL_CC) nice -n 19 ionice -c 3 cargo test --locked --no-run --bin p04-analysis-launcher --target x86_64-unknown-linux-musl -j 2
	@printf '%s\n' 'Compiled P04 analysis launcher unit tests only; run the musl executable inside Firecracker.'

test: test-vm

test-vm:
	@test -n "$(TEST_BINARY)" || { echo 'TEST_BINARY must be an absolute musl test executable path' >&2; exit 2; }
	@case "$(TEST_BINARY)" in /*) ;; *) echo 'TEST_BINARY must be absolute' >&2; exit 2;; esac
	@test -x "$(TEST_BINARY)" || { echo 'TEST_BINARY must exist and be executable' >&2; exit 2; }
	@test -n "$(RUN_ID)" || { echo 'RUN_ID must be unique and nonempty' >&2; exit 2; }
	@test -d "$(SUBJECT_REPO)" || { echo 'SUBJECT_REPO must be an existing checkout' >&2; exit 2; }
	@test -n "$(HARNESS_BINARY)" && test -n "$(GUEST_AGENT_BINARY)" && test -n "$(HARNESS_SHA256)" && test -n "$(GUEST_AGENT_SHA256)" || { echo 'HARNESS_BINARY, GUEST_AGENT_BINARY, and their expected SHA-256 values are required' >&2; exit 2; }
	@test -x "$(HARNESS_BINARY)" && test -x "$(GUEST_AGENT_BINARY)" || { echo 'pinned controller and guest agent must be executable' >&2; exit 2; }
	@test "$$(dirname "$(HARNESS_BINARY)")" = "$$(dirname "$(GUEST_AGENT_BINARY)")" || { echo 'controller and guest agent must come from one frozen packet directory' >&2; exit 2; }
	@test "$$(sha256sum "$(HARNESS_BINARY)" | cut -d ' ' -f 1)" = "$(HARNESS_SHA256)" || { echo 'controller SHA-256 mismatch' >&2; exit 2; }
	@test "$$(sha256sum "$(GUEST_AGENT_BINARY)" | cut -d ' ' -f 1)" = "$(GUEST_AGENT_SHA256)" || { echo 'guest-agent SHA-256 mismatch' >&2; exit 2; }
	$(MAKE) -C ../firecracker-harness test-vm TEST_BINARY="$(TEST_BINARY)" RUN_ID="$(RUN_ID)" SUBJECT_REPO="$(SUBJECT_REPO)" HARNESS_BINARY="$(HARNESS_BINARY)" GUEST_AGENT_BINARY="$(GUEST_AGENT_BINARY)"

fmt:
	nice -n 19 ionice -c 3 rustfmt --edition 2024 --check src/main.rs src/terrabase.rs src/analysis.rs src/p04_analysis_launcher.rs src/bin/p04_three_way.rs

format:
	nice -n 19 ionice -c 3 rustfmt --edition 2024 src/main.rs src/terrabase.rs src/analysis.rs src/p04_analysis_launcher.rs src/bin/p04_three_way.rs
