.PHONY: check contracts generated-check package-check

CONTRACT_VERSION_FILE := CONTRACT_VERSION
CONTRACT_VERSION := $(shell tr -d '[:space:]' < "$(CONTRACT_VERSION_FILE)" 2>/dev/null)
CONTRACT_DIR := contracts/$(CONTRACT_VERSION)
GENERATED_DIR := src/TerraScale.DotNet/Generated

check: contracts generated-check
	dotnet format TerraScale.DotNet.slnx --verify-no-changes
	dotnet build TerraScale.DotNet.slnx --configuration Release
	dotnet test TerraScale.DotNet.slnx --configuration Release --no-build
	$(MAKE) package-check

package-check:
	python3 scripts/check_package.py

contracts:
	@test -f "$(CONTRACT_VERSION_FILE)" || { echo "missing pinned contract version: $(CONTRACT_VERSION_FILE)" >&2; exit 1; }
	@case "$(CONTRACT_VERSION)" in v[1-9]|v[1-9][0-9]*) ;; *) echo "invalid pinned contract version: $(CONTRACT_VERSION)" >&2; exit 1 ;; esac
	@if [ -z "$(CONTRACT_SOURCE)" ] && [ -z "$(CONTRACT_URL)" ]; then \
		echo "contract $(CONTRACT_VERSION) has no immutable source; set CONTRACT_URL + CONTRACT_ARCHIVE_SHA256 (or explicit CONTRACT_SOURCE for local pre-publication)" >&2; \
		exit 1; \
	fi
	@CONTRACT_SOURCE="$(CONTRACT_SOURCE)" CONTRACT_URL="$(CONTRACT_URL)" CONTRACT_ARCHIVE_SHA256="$(CONTRACT_ARCHIVE_SHA256)" CONTRACT_CORPUS_SOURCE="$(CONTRACT_CORPUS_SOURCE)" \
		python3 scripts/contracts.py --version "$(CONTRACT_VERSION)"
	@python3 scripts/generate_contract.py --contract "$(CONTRACT_DIR)" --output "$(GENERATED_DIR)" --version "$(CONTRACT_VERSION)"

generated-check:
	@python3 scripts/generate_contract.py --contract "$(CONTRACT_DIR)" --output "$(GENERATED_DIR)" --version "$(CONTRACT_VERSION)" --check
