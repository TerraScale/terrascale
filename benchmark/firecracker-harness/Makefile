SHELL := /bin/sh
CARGO ?= cargo
NICE ?= nice
IONICE ?= ionice
MUSL_CC ?= $(shell command -v x86_64-unknown-linux-musl-gcc 2>/dev/null)

.PHONY: build build-controller build-guest preflight guest-smoke guest-run clean

build: build-controller build-guest

build-controller:
	$(NICE) -n 19 $(IONICE) -c 3 $(CARGO) build --locked --release -p fc-harness

build-guest:
	@test -n "$(MUSL_CC)" || { echo "x86_64-unknown-linux-musl-gcc is required" >&2; exit 2; }
	CC_x86_64_unknown_linux_musl="$(MUSL_CC)" \
	CARGO_TARGET_X86_64_UNKNOWN_LINUX_MUSL_LINKER="$(MUSL_CC)" \
	$(NICE) -n 19 $(IONICE) -c 3 $(CARGO) build --locked --release \
		--target x86_64-unknown-linux-musl -p fc-guest-agent

preflight: build-controller
	./target/release/fc-harness preflight

guest-smoke: build
	./target/release/fc-harness smoke

guest-run: build
	@test -n "$(PROGRAM)" || { echo "PROGRAM must name a guest executable" >&2; exit 2; }
	@test -n "$(SUBJECT_REPO)" || { echo "SUBJECT_REPO must name the measured source checkout" >&2; exit 2; }
	@test -n "$(RUN_ID)" || { echo "RUN_ID must be unique" >&2; exit 2; }
	./target/release/fc-harness run --subject-repo "$(SUBJECT_REPO)" \
		--program "$(PROGRAM)" --run-id "$(RUN_ID)" -- $(ARGS)

clean:
	$(NICE) -n 19 $(IONICE) -c 3 $(CARGO) clean
