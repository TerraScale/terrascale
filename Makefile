# TerraScale monorepo targets: libs (TerraScale.Endpoints, TerraScale.Logging), auth, TerraBase and benchmark.
# Locally, run them at low CPU priority, e.g. `nice -n 19 make test-fast`; CI runs them at full priority.
# Nothing here applies nice itself.
SOLUTION := TerraScale.slnx
CONFIGURATION ?= Debug
RUNTIME ?= linux-x64
PUBLISH_DIR ?= artifacts/publish
# Extra arguments for every TUnit runner, e.g. TEST_ARGS="--treenode-filter /*/*/Name/*".
TEST_ARGS ?=
# Match Auth Fast concurrency to the available CPUs on dev hosts and CI runners.
AUTH_TEST_PARALLEL ?= $(shell nproc)
TERRABASE_TEST_PARALLEL ?= 16
# Build revision shown by ts-auth /healthz (CI passes the commit SHA).
TS_AUTH_REVISION ?=
export TS_AUTH_REVISION
TS_DOTNET_DIR ?= sdk/dotnet
# ts-auth's tests start the TerraBase built from this same commit (ts-dotnet's TerraBaseTestServer runs TERRABASE_BINARY).
TERRABASE_BINARY ?= $(CURDIR)/src/TerraBase/bin/$(CONFIGURATION)/net11.0/terrabase
REDOCLY := npx --yes @redocly/cli@2.57.0

# Upstream's source of the vendored Turso driver keeps upstream's formatting: reformatting it would break the pristine
# import and the patch replay (src/vendor/Turso.Data.Sqlite/UPSTREAM.md).
VENDORED_UPSTREAM := src/vendor/Turso.Data.Sqlite/src
ANALYZER_CHECK_PROJECT := src/TerraBase.Analyzers.BuildCheck/TerraBase.Analyzers.BuildCheck.csproj

LIBS_PROJECTS := src/TerraScale.Endpoints/TerraScale.Endpoints.csproj \
	src/TerraScale.Endpoints.Generators/TerraScale.Endpoints.Generators.csproj \
	src/TerraScale.Endpoints.Example/TerraScale.Endpoints.Example.csproj \
	src/TerraScale.Logging/TerraScale.Logging.csproj \
	tests/TerraScale.Endpoints.Tests/TerraScale.Endpoints.Tests.csproj \
	tests/TerraScale.Logging.Tests/TerraScale.Logging.Tests.csproj
LIBS_TEST_PROJECTS := tests/TerraScale.Endpoints.Tests/TerraScale.Endpoints.Tests.csproj \
	tests/TerraScale.Logging.Tests/TerraScale.Logging.Tests.csproj

AUTH_HOST_DLL := src/TerraScale.Authentication/bin/$(CONFIGURATION)/net11.0/ts-auth.dll

BENCHMARK_DOTNET_PROJECTS := benchmark/dotnet/TerraScale.Endpoints.Benchmarks/TerraScale.Endpoints.Benchmarks.csproj \
	benchmark/dotnet/TerraScale.Logging.Benchmarks/TerraScale.Logging.Benchmarks.csproj

.PHONY: restore build fmt fmt-check lint test-fast test-slow test check docker-test clean \
	libs-build libs-test libs-check libs-nativeaot-check \
	auth-build auth-audit auth-openapi-check auth-test-fast auth-test-slow auth-mail-delivery-check auth-check auth-publish auth-dev \
	terrabase-build terrabase-analyzer-check terrabase-test-fast terrabase-test-slow terrabase-e2e-local terrabase-e2e terrabase-check terrabase-run terrabase-publish \
	benchmark-build benchmark-fmt-check benchmark-check benchmark-smoke

# Global: the whole solution.
restore:
	dotnet restore $(SOLUTION) --locked-mode
	dotnet restore $(ANALYZER_CHECK_PROJECT) --locked-mode

build: restore
	dotnet build $(SOLUTION) --configuration $(CONFIGURATION) --no-restore

fmt:
	dotnet format $(SOLUTION) --exclude $(VENDORED_UPSTREAM)

fmt-check: restore
	# Style and analyzer diagnostics are enforced by build; check whitespace once here.
	dotnet format whitespace $(SOLUTION) --verify-no-changes --no-restore --exclude $(VENDORED_UPSTREAM)

# Analyzers and code style run in the build with warnings as errors.
lint: fmt-check build

test-fast: libs-test auth-test-fast terrabase-test-fast sdk-test-fast

test-slow: auth-test-slow terrabase-test-slow

test: test-fast test-slow

# Everything required before a push.
check: lint libs-check auth-check terrabase-check benchmark-check test-fast

# CI's three area stages (Dockerfile `test`, BuildKit runs them in parallel), run locally. Needs ts-dotnet next to this checkout.
docker-test:
	docker build --build-context ts-dotnet=$(TS_DOTNET_DIR) --build-arg TERRABASE_REVISION=$(shell git rev-parse HEAD) --target test .

clean:
	dotnet clean $(SOLUTION) --configuration $(CONFIGURATION)
	rm -rf artifacts

# ts-libs: TerraScale.Endpoints and TerraScale.Logging, their generator, tests and examples. libs.slnf also builds
# their benchmarks and the CLI, so libs.slnf, auth.slnf and terrabase.slnf together build the whole solution.
libs-build: restore
	dotnet build libs.slnf --configuration $(CONFIGURATION) --no-restore

libs-test: libs-build
	set -e; for p in $(LIBS_TEST_PROJECTS); do dotnet run --project $$p --configuration $(CONFIGURATION) --no-build -- --minimum-expected-tests 1 $(TEST_ARGS); done

libs-check: libs-build
	set -e; for p in $(LIBS_PROJECTS); do dotnet format style $$p --verify-no-changes --no-restore; done

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

# One production signup email, fetched from a disposable inbox; no account is verified.
auth-mail-delivery-check:
	python3 scripts/auth/signup-mail-check.py

# Self-contained ts-auth (JIT): $(PUBLISH_DIR)/ts-auth
auth-publish:
	dotnet publish src/TerraScale.Authentication --configuration Release --runtime $(RUNTIME) --self-contained --output "$(PUBLISH_DIR)"

auth-dev:
	scripts/auth/dev.sh

# Local development: TerraBase and ts-auth under Aspire (aspire/apphost.mts) with its dashboard, on loopback only. Each run
# starts from an empty TerraBase; see README.md.
.PHONY: dev
dev:
	dotnet tool restore
	TsDotnetRoot=$(abspath $(TS_DOTNET_DIR)) dotnet aspire run --apphost aspire

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
	dotnet build tests/Turso.Data.Sqlite.Tests/Turso.Data.Sqlite.Tests.csproj --configuration $(CONFIGURATION) --no-restore
	# The vendored Turso driver's regression tests (src/vendor/Turso.Data.Sqlite/UPSTREAM.md), then the Fast suite.
	dotnet run --project tests/Turso.Data.Sqlite.Tests --configuration $(CONFIGURATION) --no-build
	dotnet run --project tests/TerraBase.Fast --configuration $(CONFIGURATION) --no-build -- --maximum-parallel-tests $(TERRABASE_TEST_PARALLEL) $(TEST_ARGS)

terrabase-test-slow: terrabase-build
	dotnet run --project tests/TerraBase.Slow --configuration $(CONFIGURATION) --no-build $(TEST_ARGS)

TERRABASE_E2E_RUN = dotnet run --project tests/TerraBase.E2E --configuration $(CONFIGURATION) --no-build --

E2E_LOCAL_TESTS := 3
terrabase-e2e-local: terrabase-build
	rm -f "$(E2E_RESULTS)"
	status=0; env -u ACTIONS_RUNTIME_TOKEN -u ACTIONS_RESULTS_URL -u TERRABASE_E2E_ORCH_NODES TERRABASE_E2E_RESULTS="$(E2E_RESULTS)" $(TERRABASE_E2E_RUN) --treenode-filter '/*/*/*/*[Category=local-only]' --minimum-expected-tests $(E2E_LOCAL_TESTS) --output detailed --no-ansi $(TEST_ARGS) || status=$$?; \
	jq -r 'select(.category == "fail" or .category == "skip") | "E2E \(.category): \(.test)\n\(.detail)"' "$(E2E_RESULTS)" || status=1; exit $$status
	jq -se 'length == $(E2E_LOCAL_TESTS) and all(.[]; .category == "pass" or .category == "harness")' "$(E2E_RESULTS)"

E2E_RESULTS ?= $(CURDIR)/tests/TerraBase.E2E/bin/e2e-results.jsonl
terrabase-e2e: terrabase-build
	rm -f "$(E2E_RESULTS)"
	status=0; env -u ACTIONS_RUNTIME_TOKEN -u ACTIONS_RESULTS_URL TERRABASE_E2E_RESULTS="$(E2E_RESULTS)" dotnet run --project tests/TerraBase.E2E --configuration $(CONFIGURATION) --no-build $(TEST_ARGS) || status=$$?; \
	scripts/terrabase/e2e-summary.sh report "$(E2E_RESULTS)" "$${E2E_LABEL:-local}" || status=1; exit $$status

terrabase-check: terrabase-build terrabase-analyzer-check
	scripts/terrabase/e2e-summary.sh check tests/TerraBase.E2E/COVERAGE.md tests/TerraBase.E2E/RUNS.md


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

.PHONY: terrabase-test-shard
terrabase-test-shard:
	python3 scripts/ci/terrabase-shard.py $(SHARD)

# API-coupled SDKs retain independent Fast suites and release identities.
.PHONY: sdk-test-fast sdk-dotnet-test-fast sdk-js-test-fast sdk-go-test-fast sdk-python-test-fast sdk-rust-test-fast
sdk-test-fast: sdk-dotnet-test-fast sdk-js-test-fast sdk-go-test-fast sdk-python-test-fast sdk-rust-test-fast

sdk-dotnet-test-fast:
	cd sdk/dotnet && bash scripts/test-fast.sh

sdk-js-test-fast:
	cd sdk/js && mise exec node@24.21.0 -- npm exec --yes --package=npm@11.18.0 -- npm ci --no-audit --no-fund
	cd sdk/js && mise exec node@24.21.0 -- npm exec --yes --package=npm@11.18.0 -- npm test

sdk-go-test-fast:
	cd sdk/go && go test -count=1 ./...

sdk-python-test-fast:
	cd sdk/python && uv venv --python 3.12 .venv && uv pip sync --python .venv/bin/python --require-hashes --only-binary :all: requirements-ci.txt
	cd sdk/python && uv pip install --python .venv/bin/python --no-deps --no-build-isolation -e '.[dev]' && .venv/bin/pytest -q

sdk-rust-test-fast:
	cd sdk/rust && rustup run 1.98.1 cargo test --locked

.PHONY: docs-test-fast
docs-test-fast:
	cd docs-site && bash scripts/test-fast.sh

.PHONY: dashboard-build dashboard-test-fast dashboard-test-slow dashboard-check
dashboard-build:
	$(MAKE) -f scripts/dashboard/Makefile build

dashboard-test-fast:
	bash scripts/dashboard/test-fast.sh

dashboard-test-slow:
	$(MAKE) -f scripts/dashboard/Makefile test-slow

dashboard-check:
	$(MAKE) -f scripts/dashboard/Makefile check
