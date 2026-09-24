.PHONY: help fmt check build build-guest vm-preflight vm-smoke vm-run

LOW_PRIORITY := nice -n 19 ionice -c 3

help:
	@printf '%s\n' 'fmt/check/build: Rust driver format, compile check, host release build' 'build-guest: static musl release binary for the guest (requires musl C compiler)' 'vm-preflight: verify Firecracker/KVM and pinned guest inputs' 'vm-smoke: boot the pinned guest and run a harmless guest command' 'vm-run: execute PROGRAM in a guest; pass PROGRAM, SUBJECT_REPO, and RUN_ID'

fmt:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust fmt

check:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust check

build:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust build

build-guest:
	$(LOW_PRIORITY) $(MAKE) -C terra-rust build-guest

vm-preflight:
	$(LOW_PRIORITY) $(MAKE) -C firecracker-harness preflight

vm-smoke:
	$(LOW_PRIORITY) $(MAKE) -C firecracker-harness guest-smoke

vm-run:
	$(LOW_PRIORITY) $(MAKE) -C firecracker-harness guest-run
