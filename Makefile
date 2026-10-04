# TerraBase .NET targets. Locally, run them at low CPU priority, e.g.
# `nice -n 19 make test-fast`; CI runs them at full priority. Nothing here
# applies nice itself.
SOLUTION := TerraScale.TerraBase.slnx
CONFIGURATION ?= Debug
RUNTIME ?= linux-x64

.PHONY: restore build fmt fmt-check lint test-fast test-slow check run publish clean

restore:
	dotnet restore $(SOLUTION)

build:
	dotnet build $(SOLUTION) --configuration $(CONFIGURATION) --nologo

fmt:
	dotnet format $(SOLUTION)

fmt-check:
	dotnet format $(SOLUTION) --verify-no-changes

# Analyzers and code style run in the build with warnings as errors.
lint: fmt-check build

test-fast: build
	dotnet run --project src/Tests/TerraScale.TerraBase.Fast --configuration $(CONFIGURATION) --no-build

test-slow: build
	dotnet run --project src/Tests/TerraScale.TerraBase.Slow --configuration $(CONFIGURATION) --no-build

# Everything required before a push.
check: lint test-fast

run:
	dotnet run --project src/TerraScale.TerraBase --configuration $(CONFIGURATION)

# Self-contained single-file binary (JIT): artifacts/publish/terrabase
publish:
	dotnet publish src/TerraScale.TerraBase --configuration Release --runtime $(RUNTIME) --self-contained --output artifacts/publish --nologo

clean:
	dotnet clean $(SOLUTION) --configuration $(CONFIGURATION) --nologo
	rm -rf artifacts
