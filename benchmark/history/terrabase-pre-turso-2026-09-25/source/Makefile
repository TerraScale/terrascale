# Call rustup's proxies directly so rust-toolchain.toml selects both tools.
RUSTUP_BIN ?= $(HOME)/.cargo/bin
CARGO = $(RUSTUP_BIN)/cargo
RUSTC = $(RUSTUP_BIN)/rustc
NICE ?= nice
NICE_ARGS ?= -n 19
CARGO_TARGET ?= x86_64-unknown-linux-musl
MUSL_CC ?= x86_64-unknown-linux-musl-gcc
CARGO_ARGS ?= --workspace
CARGO_CHECK_TARGETS ?= --lib --bins
CARGO_TEST_TARGETS ?= --test fast
CARGO_LOCK_REFRESH_ARGS ?= -p tb-core
CARGO_LOCK_REFRESH_TARGETS ?= --lib --bins
CARGO_BUILD_JOBS ?= 2

.PHONY: build check fmt fmt-check clippy test-build test-fast-build test-slow-build lock-refresh clean

check:
	$(NICE) $(NICE_ARGS) env -u RUSTUP_TOOLCHAIN RUSTC=$(RUSTC) CARGO_BUILD_JOBS=$(CARGO_BUILD_JOBS) $(CARGO) check $(CARGO_ARGS) $(CARGO_CHECK_TARGETS) --locked

build:
	$(NICE) $(NICE_ARGS) env -u RUSTUP_TOOLCHAIN RUSTC=$(RUSTC) CARGO_BUILD_JOBS=$(CARGO_BUILD_JOBS) CC_x86_64_unknown_linux_musl=$(MUSL_CC) CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER=$(MUSL_CC) $(CARGO) build $(CARGO_ARGS) --target $(CARGO_TARGET) --locked

fmt:
	$(NICE) $(NICE_ARGS) env -u RUSTUP_TOOLCHAIN RUSTC=$(RUSTC) $(CARGO) fmt --all

fmt-check:
	$(NICE) $(NICE_ARGS) env -u RUSTUP_TOOLCHAIN RUSTC=$(RUSTC) $(CARGO) fmt --all -- --check

clippy:
	$(NICE) $(NICE_ARGS) env -u RUSTUP_TOOLCHAIN RUSTC=$(RUSTC) CARGO_BUILD_JOBS=$(CARGO_BUILD_JOBS) $(CARGO) clippy $(CARGO_ARGS) $(CARGO_CHECK_TARGETS) --locked -- -D warnings

test-build:
	$(NICE) $(NICE_ARGS) env -u RUSTUP_TOOLCHAIN RUSTC=$(RUSTC) CARGO_BUILD_JOBS=$(CARGO_BUILD_JOBS) CC_x86_64_unknown_linux_musl=$(MUSL_CC) CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER=$(MUSL_CC) $(CARGO) test $(CARGO_ARGS) $(CARGO_TEST_TARGETS) --no-run --target $(CARGO_TARGET) --message-format=json-render-diagnostics --locked

# Build the Fast suite ELF without executing it; runtime qualification uses the pinned guest.
test-fast-build:
	$(MAKE) test-build CARGO_ARGS="-p tb-server" CARGO_TEST_TARGETS="--test fast"

# Build the separately scheduled Slow suite ELF without executing it.
test-slow-build:
	$(MAKE) test-build CARGO_ARGS="-p tb-server --features slow-tests" CARGO_TEST_TARGETS="--test slow"

# Resolve newly declared dependencies into Cargo.lock before running locked gates.
lock-refresh:
	$(NICE) $(NICE_ARGS) env -u RUSTUP_TOOLCHAIN RUSTC=$(RUSTC) CARGO_BUILD_JOBS=$(CARGO_BUILD_JOBS) $(CARGO) check $(CARGO_LOCK_REFRESH_ARGS) $(CARGO_LOCK_REFRESH_TARGETS)

clean:
	$(NICE) $(NICE_ARGS) env -u RUSTUP_TOOLCHAIN RUSTC=$(RUSTC) $(CARGO) clean
