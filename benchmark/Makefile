.DEFAULT_GOAL := help

# Preparatory TS-648 transport diagnostic against an explicitly selected TerraBase checkout.
TERRABASE_ROOT ?= ../terrabase
export TERRABASE_ROOT
DOTNET ?= dotnet
ACTOR_PYTHON ?= python3
ACTOR_ARGS ?=
.PHONY: actor-check actor-run
actor-check:
	ACTOR_TERRABASE_ROOT="$$(cd "$$TERRABASE_ROOT" && pwd -P)" && \
		cd actor-harness && \
		$(DOTNET) build ActorHarness.csproj --configuration Release -p:TerraBaseRoot="$$ACTOR_TERRABASE_ROOT" && \
		$(DOTNET) format whitespace . --folder --verify-no-changes && \
		$(DOTNET) build ../tests/actor-harness/ActorHarnessChecks.csproj --configuration Release && \
		$(DOTNET) format whitespace ../tests/actor-harness --folder --verify-no-changes && \
		$(ACTOR_PYTHON) ../tests/actor-harness.py --dotnet="$(DOTNET)" --terrabase-root="$$ACTOR_TERRABASE_ROOT"

actor-run:
	ACTOR_TERRABASE_ROOT="$$(cd "$$TERRABASE_ROOT" && pwd -P)" && \
		cd actor-harness && ACTOR_SOURCE_REVISION="$$(git -C "$$ACTOR_TERRABASE_ROOT" rev-parse --verify HEAD)" && \
		ACTOR_HARNESS_REVISION="$$(git rev-parse --verify HEAD)" && \
		export ACTOR_SOURCE_REVISION ACTOR_HARNESS_REVISION && \
		$(DOTNET) run --project ActorHarness.csproj --configuration Release -p:TerraBaseRoot="$$ACTOR_TERRABASE_ROOT" -- $(ACTOR_ARGS)
.PHONY: help fast lint-fast build-fast test-fast csharp-smoke deployed-smoke load-check fmt check lint build build-guest check-engine lint-engine build-guest-engine test-build test test-vm vm-preflight vm-smoke vm-network-smoke vm-run local-engine-lock local-engine-fmt local-engine-check local-engine-lint local-engine-build local-engine-build-guest

help:
	@printf '%s\n' 'load-check: stdlib mixed-load engine formatting, vet, build and race tests'
	@printf '%s\n' 'fmt/check/lint/build: C# HTTPS driver format, compile check, strict Clippy, host release build' 'build-guest: static musl SQLite-only guest binary (requires musl C compiler)' 'check-engine/lint-engine/build-guest-engine: feature-enabled SQLite and TerraBase caller' 'local-engine-*: standalone pinned Turso-vs-SQLite CRUD driver lock, format, check, lint, or build targets' 'test-build: compile feature-enabled musl tests without running them on the host' 'test-vm: execute TEST_BINARY inside Firecracker; requires RUN_ID and frozen harness/agent paths with SHA-256' 'vm-preflight: verify Firecracker/KVM and pinned guest inputs' 'vm-smoke: boot the pinned guest and check success/failure receipts' 'vm-network-smoke: boot one opt-in isolated-NIC guest' 'fast: current C# HTTPS acceptance smoke' 'csharp-smoke: local C# HTTPS acceptance; fresh run evidence under bench/csharp-smoke' 'deployed-smoke: rate-bounded gru-1 smoke through Cloudflare (TS-606); needs TERRASCALE_DASHBOARD_TOKEN and TERRASCALE_PROJECT_ID' 'vm-run: execute PROGRAM in a guest; pass PROGRAM, SUBJECT_REPO, RUN_ID, ARGS, and optional TIMEOUT_SECONDS'

# Sequential so a parallel make never tests missing or stale executables.
fast:
	$(MAKE) lint-fast
	$(MAKE) build-fast
	$(MAKE) test-fast

lint-fast:
	$(MAKE) -C https-document fmt lint

build-fast:
	$(MAKE) -C https-document build

test-fast:
	bash tests/fast.sh

csharp-smoke:
	bash tests/fast.sh

deployed-smoke:
	python3 https-document/deployed.py $(DEPLOYED_ARGS)

load-check:
	$(MAKE) -C load-test check

fmt:
	$(MAKE) -C https-document fmt

check:
	$(MAKE) -C https-document check

lint:
	$(MAKE) -C https-document lint

build:
	$(MAKE) -C https-document build

build-guest:
	$(MAKE) -C terra-rust build-guest

check-engine:
	$(MAKE) -C terra-rust check-engine

lint-engine:
	$(MAKE) -C terra-rust lint-engine

build-guest-engine:
	$(MAKE) -C terra-rust build-guest-engine

local-engine-lock:
	$(MAKE) -C local-engine lock

local-engine-fmt:
	$(MAKE) -C local-engine fmt

local-engine-check:
	$(MAKE) -C local-engine check

local-engine-lint:
	$(MAKE) -C local-engine lint

local-engine-build:
	$(MAKE) -C local-engine build

local-engine-build-guest:
	$(MAKE) -C local-engine build-guest

test-build:
	$(MAKE) -C terra-rust test-build

test: test-fast

test-vm:
	$(MAKE) -C terra-rust test-vm \
		TEST_BINARY="$(TEST_BINARY)" RUN_ID="$(RUN_ID)" \
		HARNESS_BINARY="$(HARNESS_BINARY)" GUEST_AGENT_BINARY="$(GUEST_AGENT_BINARY)" \
		HARNESS_SHA256="$(HARNESS_SHA256)" GUEST_AGENT_SHA256="$(GUEST_AGENT_SHA256)"

vm-preflight:
	$(MAKE) -C firecracker-harness preflight

vm-smoke:
	$(MAKE) -C firecracker-harness guest-smoke

vm-network-smoke:
	$(MAKE) -C firecracker-harness guest-network-smoke

vm-run:
	@test -n "$(PROGRAM)" || { echo "PROGRAM must name a guest executable" >&2; exit 2; }
	@test -n "$(SUBJECT_REPO)" || { echo "SUBJECT_REPO must name the measured source checkout" >&2; exit 2; }
	@test -n "$(RUN_ID)" || { echo "RUN_ID must be unique" >&2; exit 2; }
	$(MAKE) -C firecracker-harness guest-run
