# TerraBase .NET targets. Locally, run them at low CPU priority, e.g.
# `nice -n 19 make test-fast`; CI runs them at full priority. Nothing here
# applies nice itself.
SOLUTION := TerraBase.slnx
ANALYZER_CHECK_PROJECT := src/TerraBase.Analyzers.BuildCheck/TerraBase.Analyzers.BuildCheck.csproj
CONFIGURATION ?= Debug
RUNTIME ?= linux-x64
PUBLISH_DIR ?= artifacts/publish

.PHONY: restore build fmt fmt-check lint analyzer-check test-fast test-slow check run publish clean

restore:
	dotnet restore $(SOLUTION)
	dotnet restore $(ANALYZER_CHECK_PROJECT)

build: restore
	dotnet build $(SOLUTION) --configuration $(CONFIGURATION) --no-restore

fmt:
	dotnet format $(SOLUTION)

fmt-check: restore
	# Style and analyzer diagnostics are enforced by build; check whitespace once here.
	dotnet format whitespace $(SOLUTION) --verify-no-changes --no-restore

# Analyzers and code style run in the build with warnings as errors.
lint: fmt-check build

analyzer-check: restore
	@set -eu; log=$$(mktemp); trap 'rm -f "$$log"' EXIT; \
	if dotnet build $(ANALYZER_CHECK_PROJECT) --configuration $(CONFIGURATION) --no-restore -p:TB0001BuildCheck=true >"$$log" 2>&1; then \
		cat "$$log"; echo 'TB0001 build check unexpectedly succeeded' >&2; exit 1; \
	fi; \
	cat "$$log"; \
	test "$$(grep -E 'BuildCheck.cs\([0-9]+,[0-9]+\): error TB0001' "$$log" | sort -u | wc -l)" -eq 15; \
	for line in 35 36 37 38 39 40 41 42 43 53 54 55 56 65 71; do grep -q "BuildCheck.cs($$line," "$$log"; done; \
	test "$$(grep -E 'ExpectedFailures.cs\([0-9]+,[0-9]+\): error RS0030' "$$log" | sort -u | wc -l)" -eq 5; \
	for line in 8 10 12 14 16; do grep -q "ExpectedFailures.cs($$line," "$$log"; done; \
	if grep -vE 'error (TB0001|RS0030)' "$$log" | grep -q ': error '; then echo 'unexpected compiler error in analyzer build check' >&2; exit 1; fi

test-fast: build
	# Forgejo lacks GitHub's artifact API; HTML and JSON reports are generated locally.
	env -u ACTIONS_RUNTIME_TOKEN -u ACTIONS_RESULTS_URL dotnet run --project src/Tests/TerraBase.Fast --configuration $(CONFIGURATION) --no-build

test-slow: build
	env -u ACTIONS_RUNTIME_TOKEN -u ACTIONS_RESULTS_URL dotnet run --project src/Tests/TerraBase.Slow --configuration $(CONFIGURATION) --no-build

# Everything required before a push.
check: lint analyzer-check test-fast

run:
	dotnet run --project src/TerraBase --configuration $(CONFIGURATION)

# Self-contained single-file binary (JIT): $(PUBLISH_DIR)/terrabase
publish:
	dotnet publish src/TerraBase --configuration Release --runtime $(RUNTIME) --self-contained --output "$(PUBLISH_DIR)"

clean:
	dotnet clean $(SOLUTION) --configuration $(CONFIGURATION)
	rm -rf artifacts
