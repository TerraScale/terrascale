.DEFAULT_GOAL := help
.PHONY: help fmt check lint build build-guest check-engine lint-engine build-guest-engine test-build test test-vm vm-preflight vm-smoke vm-network-smoke vm-run local-engine-lock local-engine-fmt local-engine-check local-engine-lint local-engine-build local-engine-build-guest

# Local runs yield to interactive work; only CI=true builds at full priority.
LOW_PRIORITY := $(if $(filter true,$(CI)),,nice -n 19 ionice -c 3)

help:
	@printf '%s\n' 'fmt/check/lint/build: Rust driver format, compile check, strict Clippy, host release build' 'build-guest: static musl SQLite-only guest binary (requires musl C compiler)' 'check-engine/lint-engine/build-guest-engine: feature-enabled SQLite and TerraBase caller' 'local-engine-*: standalone pinned Turso-vs-SQLite CRUD driver lock, format, check, lint, or build targets' 'test-build: compile feature-enabled musl tests without running them on the host' 'test/test-vm: execute TEST_BINARY inside Firecracker; requires RUN_ID and frozen harness/agent paths with SHA-256' 'vm-preflight: verify Firecracker/KVM and pinned guest inputs' 'vm-smoke: boot the pinned guest and check success/failure receipts' 'vm-network-smoke: boot one opt-in isolated-NIC guest' 'vm-run: execute PROGRAM in a guest; pass PROGRAM, SUBJECT_REPO, RUN_ID, ARGS, and optional TIMEOUT_SECONDS'

fmt:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust fmt

check:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust check

lint:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust lint

build:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust build

build-guest:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust build-guest

check-engine:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust check-engine

lint-engine:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust lint-engine

build-guest-engine:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust build-guest-engine

local-engine-lock:
	$(LOW_PRIORITY) $(MAKE) -C local-engine lock

local-engine-fmt:
	$(LOW_PRIORITY) $(MAKE) -C local-engine fmt

local-engine-check:
	$(LOW_PRIORITY) $(MAKE) -C local-engine check

local-engine-lint:
	$(LOW_PRIORITY) $(MAKE) -C local-engine lint

local-engine-build:
	$(LOW_PRIORITY) $(MAKE) -C local-engine build

local-engine-build-guest:
	$(LOW_PRIORITY) $(MAKE) -C local-engine build-guest

test-build:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust test-build

test: test-vm

test-vm:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust test-vm \
		TEST_BINARY="$(TEST_BINARY)" RUN_ID="$(RUN_ID)" \
		HARNESS_BINARY="$(HARNESS_BINARY)" GUEST_AGENT_BINARY="$(GUEST_AGENT_BINARY)" \
		HARNESS_SHA256="$(HARNESS_SHA256)" GUEST_AGENT_SHA256="$(GUEST_AGENT_SHA256)"

vm-preflight:
	$(LOW_PRIORITY) $(MAKE) -C firecracker-harness preflight

vm-smoke:
	$(LOW_PRIORITY) $(MAKE) -C firecracker-harness guest-smoke

vm-network-smoke:
	$(LOW_PRIORITY) $(MAKE) -C firecracker-harness guest-network-smoke

vm-run:
	@test -n "$(PROGRAM)" || { echo "PROGRAM must name a guest executable" >&2; exit 2; }
	@test -n "$(SUBJECT_REPO)" || { echo "SUBJECT_REPO must name the measured source checkout" >&2; exit 2; }
	@test -n "$(RUN_ID)" || { echo "RUN_ID must be unique" >&2; exit 2; }
	$(LOW_PRIORITY) $(MAKE) -C firecracker-harness guest-run
