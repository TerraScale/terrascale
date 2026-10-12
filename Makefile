# TerraScale monorepo targets: libs (TerraScale.Endpoints, TerraScale.Logging), auth, TerraBase and benchmark.
# Locally, run them at low CPU priority, e.g. `nice -n 19 make test-fast`; CI runs them at full priority.
# Nothing here applies nice itself.
SOLUTION := TerraScale.slnx
CONFIGURATION ?= Debug
RUNTIME ?= linux-x64
PUBLISH_DIR ?= artifacts/publish
# Extra arguments for every TUnit runner, e.g. TEST_ARGS="--treenode-filter /*/*/Name/*".
TEST_ARGS ?=
# Parallel cap of each Fast runner, overridable for a shared machine (for example AUTH_TEST_PARALLEL=16).
# Matches the 4-vCPU runners. At 2 the auth suite averaged under one busy core.
AUTH_TEST_PARALLEL ?= 4
TERRABASE_TEST_PARALLEL ?= 16
# Build revision shown by ts-auth /healthz (CI passes the commit SHA).
TS_AUTH_REVISION ?=
export TS_AUTH_REVISION
TS_DOTNET_DIR ?= ../ts-dotnet
# ts-auth's tests start the TerraBase built from this same commit (ts-dotnet's TerraBaseTestServer runs TERRABASE_BINARY).
TERRABASE_BINARY ?= $(CURDIR)/src/TerraBase/bin/$(CONFIGURATION)/net11.0/terrabase
REDOCLY := npx --yes @redocly/cli@2.57.0

ANALYZER_CHECK_PROJECT := src/TerraBase.Analyzers.BuildCheck/TerraBase.Analyzers.BuildCheck.csproj

LIBS_PROJECTS := src/TerraScale.Endpoints/TerraScale.Endpoints.csproj \
	src/TerraScale.Endpoints.Generators/TerraScale.Endpoints.Generators.csproj \
	src/TerraScale.Endpoints.Example/TerraScale.Endpoints.Example.csproj \
	src/TerraScale.Logging/TerraScale.Logging.csproj \
	tests/TerraScale.Endpoints.Tests/TerraScale.Endpoints.Tests.csproj \
	tests/TerraScale.Logging.Tests/TerraScale.Logging.Tests.csproj
LIBS_TEST_PROJECTS := tests/TerraScale.Endpoints.Tests/TerraScale.Endpoints.Tests.csproj \
	tests/TerraScale.Logging.Tests/TerraScale.Logging.Tests.csproj
# Only TerraScale.Logging is published: ts-reviewer consumes it outside this repository.
LIBS_PACKAGE := src/TerraScale.Logging/TerraScale.Logging.csproj
PACKAGE_VERSION = $(shell bash scripts/libs/package-version.sh)

AUTH_HOST_DLL := src/TerraScale.Authentication/bin/$(CONFIGURATION)/net11.0/ts-auth.dll

BENCHMARK_DOTNET_PROJECTS := benchmark/dotnet/TerraScale.Endpoints.Benchmarks/TerraScale.Endpoints.Benchmarks.csproj \
	benchmark/dotnet/TerraScale.Logging.Benchmarks/TerraScale.Logging.Benchmarks.csproj

.PHONY: restore build fmt fmt-check lint test-fast test-slow test check docker-test clean \
	libs-build libs-test handoff-test libs-check libs-pack libs-nativeaot-check \
	auth-build auth-audit auth-openapi-check auth-test-fast auth-test-slow auth-check auth-publish auth-dev \
	terrabase-build terrabase-analyzer-check terrabase-test-fast terrabase-test-slow terrabase-e2e terrabase-e2e-fly-outage terrabase-check terrabase-run terrabase-publish \
	benchmark-build benchmark-fmt-check benchmark-check benchmark-smoke benchmark-repl-local benchmark-repl-fly

# Global: the whole solution.
restore:
	dotnet restore $(SOLUTION) --locked-mode
	dotnet restore $(ANALYZER_CHECK_PROJECT) --locked-mode

build: restore
	dotnet build $(SOLUTION) --configuration $(CONFIGURATION) --no-restore

fmt:
	dotnet format $(SOLUTION)

fmt-check: restore
	# Style and analyzer diagnostics are enforced by build; check whitespace once here.
	dotnet format whitespace $(SOLUTION) --verify-no-changes --no-restore

# Analyzers and code style run in the build with warnings as errors.
lint: fmt-check build

test-fast: libs-test auth-test-fast terrabase-test-fast

test-slow: auth-test-slow terrabase-test-slow

test: test-fast test-slow

# Everything required before a push.
check: lint libs-check auth-check terrabase-check benchmark-check test-fast

# CI's three area stages (Dockerfile `test`, BuildKit runs them in parallel), run locally. Needs ts-dotnet next to this checkout.
docker-test:
	docker build --build-context ts-dotnet=$(TS_DOTNET_DIR) --target test .

clean:
	dotnet clean $(SOLUTION) --configuration $(CONFIGURATION)
	rm -rf artifacts

# ts-libs: TerraScale.Endpoints and TerraScale.Logging, their generator, tests and examples. libs.slnf also builds
# their benchmarks and the CLI, so libs.slnf, auth.slnf and terrabase.slnf together build the whole solution.
libs-build: restore
	dotnet build libs.slnf --configuration $(CONFIGURATION) --no-restore

libs-test: libs-build
	set -e; for p in $(LIBS_TEST_PROJECTS); do dotnet run --project $$p --configuration $(CONFIGURATION) --no-build -- --minimum-expected-tests 1 $(TEST_ARGS); done

libs-check: libs-build handoff-test
	set -e; for p in $(LIBS_PROJECTS); do dotnet format style $$p --verify-no-changes --no-restore; done

# The user-run credential entry point (scripts/handoff) and the e2e credential script, against fake flyctl, gh and curl.
handoff-test:
	bash scripts/handoff/morning-credentials-test.sh

libs-pack: restore
	bash scripts/libs/package-version-test.sh
	rm -rf -- artifacts/packages
	dotnet pack $(LIBS_PACKAGE) --configuration Release --nologo --output artifacts/packages -p:Version=$(PACKAGE_VERSION)
	ls -1 artifacts/packages
	set -e; ls artifacts/packages/*.nupkg >/dev/null; for pkg in artifacts/packages/*.nupkg; do case "$$pkg" in *".$(PACKAGE_VERSION).nupkg") ;; *) echo "$$pkg is not the calendar version $(PACKAGE_VERSION)" >&2; exit 1;; esac; done

# Publishes the in-repo example with native AOT: the generated registration and the endpoint pipeline must trim and AOT-compile.
libs-nativeaot-check: restore
	dotnet publish src/TerraScale.Endpoints.Example/TerraScale.Endpoints.Example.csproj --configuration Release --runtime $(RUNTIME) --output artifacts/nativeaot/endpoints-example

# ts-auth.
auth-build: restore
	dotnet build auth.slnf --configuration $(CONFIGURATION) --no-restore --nologo

auth-audit:
	dotnet run scripts/auth/check-nuget-vulnerabilities.cs src/TerraScale.Authentication/TerraScale.Authentication.csproj

# The served document, and the platform contract (TS-598) that later slices implement.
auth-openapi-check: auth-build
	mkdir -p artifacts
	scripts/auth/export-openapi.sh $(AUTH_HOST_DLL) artifacts/openapi.json
	$(REDOCLY) lint artifacts/openapi.json docs/auth/platform-api.openapi.yaml

# Builds only the Fast suite and its references; the Slow suite has its own target.
# Cases share one TerraBase process; individual concurrency tests still run their own request races.
auth-test-fast: restore
	dotnet build src/TerraBase/TerraBase.csproj --configuration $(CONFIGURATION) --no-restore --nologo
	dotnet build tests/TerraScale.Authentication.Fast/TerraScale.Authentication.Fast.csproj --configuration $(CONFIGURATION) --no-restore --nologo
	TERRABASE_BINARY=$(TERRABASE_BINARY) dotnet run --project tests/TerraScale.Authentication.Fast --configuration $(CONFIGURATION) --no-build -- --maximum-parallel-tests $(AUTH_TEST_PARALLEL) $(TEST_ARGS)

auth-test-slow: auth-build
	dotnet build src/TerraBase/TerraBase.csproj --configuration $(CONFIGURATION) --no-restore --nologo
	TERRABASE_BINARY=$(TERRABASE_BINARY) dotnet run --project tests/TerraScale.Authentication.Slow --configuration $(CONFIGURATION) --no-build $(TEST_ARGS)

auth-check: auth-audit auth-openapi-check

# Self-contained ts-auth (JIT): $(PUBLISH_DIR)/ts-auth
auth-publish:
	dotnet publish src/TerraScale.Authentication --configuration Release --runtime $(RUNTIME) --self-contained --output "$(PUBLISH_DIR)"

auth-dev:
	scripts/auth/dev.sh

# TerraBase: the server, its analyzer and its integration suites.
terrabase-build: restore
	dotnet build terrabase.slnf --configuration $(CONFIGURATION) --no-restore

terrabase-analyzer-check: restore
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

# Builds only the Fast suite: TerraBase and its analyzer build as its references; the Slow suite has its own target.
terrabase-test-fast: restore
	dotnet build tests/TerraBase.Fast/TerraBase.Fast.csproj --configuration $(CONFIGURATION) --no-restore
	dotnet run --project tests/TerraBase.Fast --configuration $(CONFIGURATION) --no-build -- --maximum-parallel-tests $(TERRABASE_TEST_PARALLEL) $(TEST_ARGS)

terrabase-test-slow: terrabase-build
	dotnet run --project tests/TerraBase.Slow --configuration $(CONFIGURATION) --no-build $(TEST_ARGS)

# End-to-end replication (tests/TerraBase.E2E): leaderless writes between gru, sjc and fra, every pair and three-way, with
# partitions, restarts, a region down, backlog and clock skew. Local by default (in-process nodes); tests/TerraBase.E2E/Gaps.cs
# records which scenarios wait on which replication slice. With TERRABASE_E2E_FLY_URL, TERRABASE_E2E_FLY_API_KEY,
# TERRABASE_E2E_FLY_PROJECT and TERRABASE_E2E_FLY_DATABASE set, the same scenarios run against Fly; the key is a TerraBase
# API key of the e2e project (documents:read/write, no expiry: revoked explicitly), installed by a human: scripts/handoff/morning-credentials.sh install e2e.
# Not part of check: CI's e2e job runs it after the deploys.
terrabase-e2e: terrabase-build
	env -u ACTIONS_RUNTIME_TOKEN -u ACTIONS_RESULTS_URL dotnet run --project tests/TerraBase.E2E --configuration $(CONFIGURATION) --no-build $(TEST_ARGS)

# Stops machines of the Fly app (the fly-outage category): run alone, never with terrabase-e2e and never on every deploy.
terrabase-e2e-fly-outage: terrabase-build
	env -u ACTIONS_RUNTIME_TOKEN -u ACTIONS_RESULTS_URL TERRABASE_E2E_FLY_OUTAGE=1 dotnet run --project tests/TerraBase.E2E --configuration $(CONFIGURATION) --no-build --treenode-filter '/*/*/*/*[Category=fly-outage]' $(TEST_ARGS)

terrabase-check: terrabase-build terrabase-analyzer-check

terrabase-run:
	dotnet run --project src/TerraBase --configuration $(CONFIGURATION)

# Self-contained single-file binary (JIT): $(PUBLISH_DIR)/terrabase
terrabase-publish:
	dotnet publish src/TerraBase --configuration Release --runtime $(RUNTIME) --self-contained --output "$(PUBLISH_DIR)"

# ts-benchmark (Go) plus the BenchmarkDotNet projects for ts-libs.
benchmark-build: restore
	$(MAKE) -C benchmark build
	set -e; for p in $(BENCHMARK_DOTNET_PROJECTS); do dotnet build $$p --configuration $(CONFIGURATION) --no-restore; done

benchmark-fmt-check:
	$(MAKE) -C benchmark fmt-check

benchmark-check:
	$(MAKE) -C benchmark check

benchmark-smoke:
	$(MAKE) -C benchmark smoke

# Multi-region replication benchmark against three local regions in one host process (Release build): writes
# REPL_ARGS' documents (default 1,000,000) across gru, sjc and fra, verifies every region and writes
# benchmark/bench/<date>-repl-local-<docs>.md. Example: nice -n 19 make benchmark-repl-local REPL_ARGS='--docs 100000'.
benchmark-repl-local:
	$(MAKE) terrabase-build CONFIGURATION=Release
	$(MAKE) -C benchmark repl-bench-build
	benchmark/scripts/repl-local.sh

# The same benchmark on Fly: one temporary load machine per region (gru, sjc, fra) in app ts-bench, destroyed on exit.
# Needs the benchmark credential (benchmark/scripts/repl-fly-tenant.sh install, run by a user) and FLY_API_TOKEN for
# machine metrics. Flags come from REPL_ARGS.
benchmark-repl-fly:
	benchmark/scripts/repl-fly.sh $(REPL_ARGS)
