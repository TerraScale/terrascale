# ts-auth .NET targets. Locally, run them at low CPU priority, e.g.
# `nice -n 19 make test-fast`; CI runs them at full priority. Nothing here
# applies nice itself.
SOLUTION := TerraScale.Auth.slnx
CONFIGURATION ?= Debug
HOST_DLL := src/TerraScale.Auth/bin/$(CONFIGURATION)/net10.0/ts-auth.dll
export TS_AUTH_REVISION
REDOCLY := npx --yes @redocly/cli@2.57.0

.PHONY: restore build fmt fmt-check lint audit openapi-check test-fast test-slow check dev clean

restore:
	dotnet restore $(SOLUTION)

build:
	dotnet build $(SOLUTION) --configuration $(CONFIGURATION) --nologo

fmt:
	dotnet format $(SOLUTION)

fmt-check:
	dotnet format $(SOLUTION) --verify-no-changes

# Analyzers run in the build with warnings as errors.
lint: fmt-check build

audit:
	dotnet run scripts/check-nuget-vulnerabilities.cs src/TerraScale.Auth/TerraScale.Auth.csproj

# The served document, and the platform contract (TS-598) that later slices implement.
openapi-check: build
	mkdir -p artifacts
	scripts/export-openapi.sh $(HOST_DLL) artifacts/openapi.json
	$(REDOCLY) lint artifacts/openapi.json docs/platform-api.openapi.yaml

# Cases share one TerraBase process; individual concurrency tests still run their own request races.
test-fast: build
	dotnet run --project tests/TerraScale.Auth.Fast --configuration $(CONFIGURATION) --no-build -- --maximum-parallel-tests 2

test-slow: build
	dotnet run --project tests/TerraScale.Auth.Slow --configuration $(CONFIGURATION) --no-build

# Everything required before a push.
check: lint audit openapi-check test-fast

dev:
	scripts/dev.sh

clean:
	dotnet clean $(SOLUTION) --configuration $(CONFIGURATION) --nologo
	rm -rf artifacts
