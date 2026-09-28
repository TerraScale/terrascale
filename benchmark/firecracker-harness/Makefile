SHELL := /bin/sh
.DEFAULT_GOAL := help
CARGO ?= cargo
MUSL_CC ?= $(shell command -v x86_64-unknown-linux-musl-gcc 2>/dev/null)
TIMEOUT_SECONDS ?= 1800

.PHONY: help lock fmt format check-controller lint-controller check-image-builder lint-image-builder build-image-builder build-bucket-admin test test-build test-build-image-builder test-vm lint build build-controller build-guest build-cgroup-probe build-finite-cgroup-runner build-client-finite-cgroup-runner preflight guest-smoke guest-network-smoke guest-image-smoke guest-run guest-rustfs-fixture network-cleanup rustfs-fixture-cleanup clean

help:
	@printf '%s\n' 'fmt/check-controller/lint-controller: format, compile, and lint the harness and guest agent' 'check-image-builder/lint-image-builder: compile and lint the image builder' 'build-image-builder: build the host CLI used for candidate input provisioning' 'build: release controller and static musl guest agent' 'build-bucket-admin: static musl Rust S3 bucket lifecycle helper' 'build-cgroup-probe: static musl read-only cgroup-v2 guest probe' 'build-finite-cgroup-runner: static musl finite-memory guest test wrapper' 'build-client-finite-cgroup-runner: static musl finite-memory client test wrapper' 'test-build/test-build-image-builder: compile musl tests without running them on the host' 'test/test-vm: execute TEST_BINARY inside Firecracker; requires RUN_ID' 'preflight/guest-smoke/guest-network-smoke/guest-image-smoke: verify pinned VM inputs and boot paths' 'guest-run: execute PROGRAM in a guest; requires SUBJECT_REPO and RUN_ID' 'guest-rustfs-fixture: two private VMs, qualified RustFS artifact and real Slow S3 caller' 'network-cleanup/rustfs-fixture-cleanup: reconcile only recorded network resources using RECEIPT'

lock:
	$(CARGO) generate-lockfile

fmt:
	$(CARGO) fmt --all --check

format:
	$(CARGO) fmt --all

check-controller:
	$(CARGO) check --locked -p fc-harness -p fc-guest-agent

lint-controller:
	$(CARGO) clippy --locked --all-targets -p fc-harness -p fc-guest-agent -- -D warnings

check-image-builder:
	$(CARGO) check --locked -p fc-image-builder

lint-image-builder:
	$(CARGO) clippy --locked --all-targets -p fc-image-builder -- -D warnings

build-image-builder:
	$(CARGO) build --locked -p fc-image-builder

test: test-vm

# Compile tests for the guest. The harness must execute them inside Firecracker.
test-build:
	@test -n "$(MUSL_CC)" || { echo "x86_64-unknown-linux-musl-gcc is required" >&2; exit 2; }
	CC_x86_64_unknown_linux_musl="$(MUSL_CC)" \
	CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER="$(MUSL_CC)" \
	$(CARGO) test --locked --no-run \
		--target x86_64-unknown-linux-musl -p fc-harness
	@printf '%s\n' 'Compiled guest test executable only; run make test-vm with TEST_BINARY and RUN_ID to execute inside Firecracker.'

test-build-image-builder:
	@test -n "$(MUSL_CC)" || { echo "x86_64-unknown-linux-musl-gcc is required" >&2; exit 2; }
	CC_x86_64_unknown_linux_musl="$(MUSL_CC)" \
	CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER="$(MUSL_CC)" \
	$(CARGO) test --locked --no-run --lib \
		--target x86_64-unknown-linux-musl -p fc-image-builder
	@printf '%s\n' 'Compiled image-builder tests only; run the printed musl executable with make test-vm inside Firecracker.'

test-vm:
	@test -n "$(TEST_BINARY)" || { echo "TEST_BINARY must name the compiled musl test executable" >&2; exit 2; }
	@test -n "$(RUN_ID)" || { echo "RUN_ID must be unique" >&2; exit 2; }
	FC_BENCHMARK_REPO="$(abspath ..)" \
	FC_GUEST_AGENT_PATH="$(or $(GUEST_AGENT_BINARY),./target/x86_64-unknown-linux-musl/release/fc-guest-agent)" \
		"$(or $(HARNESS_BINARY),./target/release/fc-harness)" run \
		--benchmark-repo "$(abspath ..)" \
		--subject-repo "$(or $(SUBJECT_REPO),$(abspath ..))" \
		--program "$(TEST_BINARY)" --run-id "$(RUN_ID)" \
		--timeout-seconds 120 -- --nocapture

lint:
	$(CARGO) clippy --locked --all-targets -- -D warnings

build: build-controller build-guest

build-controller:
	$(CARGO) build --locked --release -p fc-harness

build-guest:
	@test -n "$(MUSL_CC)" || { echo "x86_64-unknown-linux-musl-gcc is required" >&2; exit 2; }
	CC_x86_64_unknown_linux_musl="$(MUSL_CC)" \
	CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER="$(MUSL_CC)" \
	$(CARGO) build --locked --release \
		--target x86_64-unknown-linux-musl -p fc-guest-agent

build-bucket-admin:
	@test -n "$(MUSL_CC)" || { echo "x86_64-unknown-linux-musl-gcc is required" >&2; exit 2; }
	CC_x86_64_unknown_linux_musl="$(MUSL_CC)" \
	CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER="$(MUSL_CC)" \
	$(CARGO) build --locked --release \
		--target x86_64-unknown-linux-musl -p rustfs-bucket-admin

build-cgroup-probe:
	@test -n "$(MUSL_CC)" || { echo "x86_64-unknown-linux-musl-gcc is required" >&2; exit 2; }
	CC_x86_64_unknown_linux_musl="$(MUSL_CC)" \
	CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER="$(MUSL_CC)" \
	$(CARGO) build --locked --release \
		--target x86_64-unknown-linux-musl \
		-p fc-harness --example cgroup_probe

build-finite-cgroup-runner:
	@test -n "$(MUSL_CC)" || { echo "x86_64-unknown-linux-musl-gcc is required" >&2; exit 2; }
	CC_x86_64_unknown_linux_musl="$(MUSL_CC)" \
	CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER="$(MUSL_CC)" \
	$(CARGO) build --locked --release \
		--target x86_64-unknown-linux-musl \
		-p fc-harness --example finite_cgroup_runner

build-client-finite-cgroup-runner:
	@test -n "$(MUSL_CC)" || { echo "x86_64-unknown-linux-musl-gcc is required" >&2; exit 2; }
	CC_x86_64_unknown_linux_musl="$(MUSL_CC)" \
	CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER="$(MUSL_CC)" \
	$(CARGO) build --locked --release \
		--target x86_64-unknown-linux-musl \
		-p fc-harness --example client_finite_cgroup_runner

preflight: build-controller
	FC_BENCHMARK_REPO="$(abspath ..)" ./target/release/fc-harness preflight

guest-smoke: build
	FC_BENCHMARK_REPO="$(abspath ..)" ./target/release/fc-harness smoke

guest-network-smoke: build
	FC_BENCHMARK_REPO="$(abspath ..)" ./target/release/fc-harness network-smoke

guest-image-smoke: build
	@test -n "$(IMAGE_PROFILE)" || { echo "IMAGE_PROFILE must name a verified image-manifest.json" >&2; exit 2; }
	FC_BENCHMARK_REPO="$(abspath ..)" ./target/release/fc-harness image-smoke "$(IMAGE_PROFILE)"

network-cleanup:
	@test -n "$(RECEIPT)" || { echo "RECEIPT must name a network-ownership.json file" >&2; exit 2; }
	./target/release/fc-harness cleanup-network "$(RECEIPT)"

rustfs-fixture-cleanup:
	@test -n "$(RECEIPT)" || { echo "RECEIPT must name a pair-network-ownership.json file" >&2; exit 2; }
	./target/release/fc-harness cleanup-rustfs-fixture "$(RECEIPT)"

guest-rustfs-fixture: build-controller build-guest build-bucket-admin
	@test -n "$(SUBJECT_REPO)" || { echo "SUBJECT_REPO is required" >&2; exit 2; }
	@test -n "$(CLIENT_PROGRAM)" || { echo "CLIENT_PROGRAM must name the Slow integration executable" >&2; exit 2; }
	@test -n "$(RUSTFS_ARTIFACT_RECEIPT)" || { echo "RUSTFS_ARTIFACT_RECEIPT is required" >&2; exit 2; }
	@test -n "$(IMAGE_PROFILE)" || { echo "IMAGE_PROFILE is required" >&2; exit 2; }
	@test -n "$(RUN_ID)" || { echo "RUN_ID must be unique" >&2; exit 2; }
	FC_GUEST_AGENT_PATH="./target/x86_64-unknown-linux-musl/release/fc-guest-agent" \
		./target/release/fc-harness rustfs-fixture --benchmark-repo "$(abspath ..)" \
		--subject-repo "$(SUBJECT_REPO)" --client-program "$(CLIENT_PROGRAM)" \
		--bucket-admin-program "./target/x86_64-unknown-linux-musl/release/rustfs-bucket-admin" \
		--artifact-receipt "$(RUSTFS_ARTIFACT_RECEIPT)" --image-profile "$(IMAGE_PROFILE)" \
		--run-id "$(RUN_ID)" --timeout-seconds "$(TIMEOUT_SECONDS)"

guest-run: build
	@test -n "$(PROGRAM)" || { echo "PROGRAM must name a guest executable" >&2; exit 2; }
	@test -n "$(SUBJECT_REPO)" || { echo "SUBJECT_REPO must name the measured source checkout" >&2; exit 2; }
	@test -n "$(RUN_ID)" || { echo "RUN_ID must be unique" >&2; exit 2; }
	FC_BENCHMARK_REPO="$(abspath ..)" ./target/release/fc-harness run \
		--benchmark-repo "$(abspath ..)" --subject-repo "$(SUBJECT_REPO)" \
		--program "$(PROGRAM)" --run-id "$(RUN_ID)" \
		--timeout-seconds "$(TIMEOUT_SECONDS)" \
		$(if $(IMAGE_PROFILE),--image-profile "$(IMAGE_PROFILE)") \
		$(if $(PAYLOAD_FILE),--payload-file "$(PAYLOAD_FILE)") -- $(ARGS)

clean:
	$(CARGO) clean
