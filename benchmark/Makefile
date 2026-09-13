.PHONY: check build test contracts contract-archive

SOLUTION ?= TerraBase.slnx
CONFIGURATION ?= Release
CONTRACT_VERSION ?= v1
CONTRACT_PREVIOUS ?=
CONTRACT_ARCHIVE_DIR ?= contract-archives
CONTRACT_ARCHIVE := $(CONTRACT_ARCHIVE_DIR)/$(CONTRACT_VERSION).tar

# The root solution is the canonical C# build and test boundary. Keep the
# process at the lowest priority so local development remains responsive.
check: contracts build test

build:
	nice -n 19 dotnet build "$(SOLUTION)" --configuration "$(CONFIGURATION)" --nologo

test:
	nice -n 19 dotnet test "$(SOLUTION)" --configuration "$(CONFIGURATION)" --no-build --nologo

# Validate the checked-in producer bundle without invoking a retired Rust
# generator. The archive packager validates manifest paths, file types,
# digests, and immutable output bytes; a temporary output keeps this target
# read-only with respect to the checked-in archive.

contracts:
	@work="$$(mktemp -d "$${TMPDIR:-/tmp}/terrabase-contracts.XXXXXX")"; \
	trap 'rm -rf -- "$$work"' EXIT; \
	python3 scripts/package-contract-archive.py \
		--source contracts --version "$(CONTRACT_VERSION)" \
		--output "$$work/$(CONTRACT_VERSION).tar" >/dev/null; \
	if [ -n "$(CONTRACT_PREVIOUS)" ]; then \
		echo "CONTRACT_PREVIOUS is unsupported by the C# producer bundle validator" >&2; \
		exit 2; \
	fi

contract-archive: contracts
	python3 scripts/package-contract-archive.py \
		--source contracts --version "$(CONTRACT_VERSION)" \
		--output "$(CONTRACT_ARCHIVE)"
