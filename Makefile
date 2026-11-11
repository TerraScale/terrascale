# TerraScale monorepo targets: libs (TerraScale.Endpoints, TerraScale.Logging), auth, TerraBase and benchmark.
SOLUTION := TerraScale.slnx
CONFIGURATION ?= Debug
RUNTIME ?= linux-x64
PUBLISH_DIR ?= artifacts/publish
# Build revision shown by ts-auth /healthz (CI passes the commit SHA).
TS_AUTH_REVISION ?=
export TS_AUTH_REVISION
TS_DOTNET_DIR ?= sdk/dotnet
TERRABASE_BINARY ?= $(CURDIR)/src/TerraBase/bin/$(CONFIGURATION)/net11.0/terrabase
REDOCLY := npx --yes @redocly/cli@2.57.0

# Upstream's source of the vendored SQL driver keeps upstream's formatting: reformatting it would break the pristine
# import and the patch replay (src/vendor/Turso.Data.Sqlite/UPSTREAM.md).
VENDORED_UPSTREAM := src/vendor/Turso.Data.Sqlite/src

AUTH_HOST_DLL := src/TerraScale.Authentication/bin/$(CONFIGURATION)/net11.0/ts-auth.dll

BENCHMARK_DOTNET_PROJECTS := benchmark/dotnet/TerraScale.Endpoints.Benchmarks/TerraScale.Endpoints.Benchmarks.csproj \
	benchmark/dotnet/TerraScale.EntityFrameworkCore.Benchmarks/TerraScale.EntityFrameworkCore.Benchmarks.csproj \
	benchmark/dotnet/TerraScale.Logging.Benchmarks/TerraScale.Logging.Benchmarks.csproj

.PHONY: restore turso-native build fmt fmt-check lint check clean \
	libs-build libs-check libs-nativeaot-check \
	auth-restore auth-build auth-audit auth-openapi-check auth-check auth-publish auth-dev \
	terrabase-build terrabase-run terrabase-publish \
	benchmark-build benchmark-fmt-check benchmark-check region-sim

# Global: the whole solution.
restore:
	dotnet restore $(SOLUTION) --locked-mode

# Pinned storage engine native libraries (src/vendor/Turso.Native/SHA256SUMS), needed by every build of TerraBase or its driver.
# Fetched only when the pins do not verify: a fresh checkout has none, and a partial or stale copy is replaced. The
# download checks the same pins before it publishes a file.
TURSO_NATIVE_DIR := src/vendor/Turso.Data.Sqlite/native
turso-native:
	scripts/vendor/verify-turso-native.sh $(TURSO_NATIVE_DIR) >/dev/null 2>&1 || scripts/vendor/download-turso-native.sh $(TURSO_NATIVE_DIR)

build: restore turso-native
	dotnet build $(SOLUTION) --configuration $(CONFIGURATION) --no-restore

fmt:
	dotnet format $(SOLUTION) --exclude $(VENDORED_UPSTREAM)

fmt-check: restore
	# Style and analyzer diagnostics are enforced by build; check whitespace once here.
	dotnet format whitespace $(SOLUTION) --verify-no-changes --no-restore --exclude $(VENDORED_UPSTREAM)

# Analyzers and code style run in the build with warnings as errors.
lint: fmt-check build

# Everything required before a push.
check: lint libs-check auth-check benchmark-check

clean:
	dotnet clean $(SOLUTION) --configuration $(CONFIGURATION)
	rm -rf artifacts

libs-build: restore
	dotnet build libs.slnf --configuration $(CONFIGURATION) --no-restore

libs-check: libs-build
	dotnet format style libs.slnf --verify-no-changes --no-restore --exclude benchmark/dotnet

# Publishes the in-repo example with native AOT: the generated registration and the endpoint pipeline must trim and AOT-compile.
libs-nativeaot-check: restore
	dotnet publish src/TerraScale.Endpoints.Example/TerraScale.Endpoints.Example.csproj --configuration Release --runtime $(RUNTIME) --output artifacts/nativeaot/endpoints-example

# ts-auth.
auth-restore:
	dotnet restore auth.slnf --locked-mode

auth-build: auth-restore turso-native
	dotnet build auth.slnf --configuration $(CONFIGURATION) --no-restore --nologo -m

auth-audit: auth-restore
	dotnet run scripts/auth/check-nuget-vulnerabilities.cs src/TerraScale.Authentication/TerraScale.Authentication.csproj

# The served OpenAPI schema, and the platform contract (TS-598) that later slices implement.
auth-openapi-check: auth-build
	mkdir -p artifacts
	scripts/auth/export-openapi.sh $(AUTH_HOST_DLL) artifacts/openapi.json
	$(REDOCLY) lint artifacts/openapi.json docs/auth/platform-api.openapi.yaml

auth-check: auth-audit auth-openapi-check

# Self-contained ts-auth (JIT): $(PUBLISH_DIR)/ts-auth
auth-publish:
	dotnet publish src/TerraScale.Authentication --configuration Release --runtime $(RUNTIME) --self-contained --output "$(PUBLISH_DIR)"

auth-dev:
	scripts/auth/dev.sh

# Local development: TerraBase and ts-auth under Aspire (aspire/apphost.mts) with its dashboard, on loopback only. Each run
# starts from an empty TerraBase; see README.md.
.PHONY: dev
dev: turso-native
	dotnet tool restore
	TsDotnetRoot=$(abspath $(TS_DOTNET_DIR)) dotnet aspire run --apphost aspire

terrabase-build: restore turso-native
	dotnet build terrabase.slnf --configuration $(CONFIGURATION) --no-restore

terrabase-run: turso-native
	dotnet run --project src/TerraBase --configuration $(CONFIGURATION)

# Self-contained single-file binary (JIT): $(PUBLISH_DIR)/terrabase
terrabase-publish: turso-native
	dotnet publish src/TerraBase --configuration Release --runtime $(RUNTIME) --self-contained --output "$(PUBLISH_DIR)"
	test ! -e "$(PUBLISH_DIR)/libturso_sdk_kit.so"
	test ! -e "$(PUBLISH_DIR)/libterrascale_sql_parser.so"

# ts-benchmark (Go) plus the BenchmarkDotNet projects for ts-libs.
benchmark-build: restore
	$(MAKE) -C benchmark build
	set -e; for p in $(BENCHMARK_DOTNET_PROJECTS); do dotnet build $$p --configuration $(CONFIGURATION) --no-restore; done

benchmark-fmt-check:
	$(MAKE) -C benchmark fmt-check

benchmark-check:
	$(MAKE) -C benchmark check

# Two-region replica simulation: gru-1 and sjc-1 TerraBase nodes driven by the ts CLI (benchmark/region-sim/README.md).
REGION_SIM_OUT ?= $(CURDIR)/benchmark/bench/region-sim/$(shell date -u +%Y-%m-%d-%H%M).md
region-sim: terrabase-build
	cd sdk/go && go build -trimpath -o $(CURDIR)/benchmark/bin/ts ./cmd/ts
	cd benchmark && go build -trimpath -o bin/region-sim ./cmd/region-sim
	benchmark/bin/region-sim -terrabase $(TERRABASE_BINARY) -ts benchmark/bin/ts -out $(REGION_SIM_OUT) $(REGION_SIM_ARGS)

.PHONY: landing-build
landing-build:
	cd landing-site && npm ci --no-audit --no-fund && npm run build

.PHONY: status-build
status-build:
	cd status-site && npm ci --no-audit --no-fund && npm run check && npm run lint && npm run build

.PHONY: sdk-check sdk-dotnet-check sdk-js-check sdk-go-check sdk-python-check sdk-rust-check
sdk-check: sdk-dotnet-check sdk-js-check sdk-go-check sdk-python-check sdk-rust-check

sdk-dotnet-check:
	cd sdk/dotnet && dotnet restore TerraScale.DotNet.slnx --locked-mode
	cd sdk/dotnet && dotnet build TerraScale.DotNet.slnx --configuration Release --no-restore

sdk-js-check:
	cd sdk/js && mise exec node@24.21.0 -- npm exec --yes --package=npm@11.18.0 -- npm ci --no-audit --no-fund
	cd sdk/js && mise exec node@24.21.0 -- npm run typecheck
	cd sdk/js && mise exec node@24.21.0 -- npm run build

sdk-go-check:
	cd sdk/go && go build ./... && go vet ./...

sdk-python-check:
	cd sdk/python && uv venv --allow-existing --python 3.12 .venv && uv pip sync --python .venv/bin/python --require-hashes --only-binary :all: requirements-ci.txt
	cd sdk/python && uv pip install --python .venv/bin/python --no-deps --no-build-isolation -e '.[dev]' && .venv/bin/mypy

sdk-rust-check:
	cd sdk/rust && cargo build --locked --all-targets

.PHONY: docs-build
docs-build:
	cd docs-site && npm ci --no-audit --no-fund && npm run build
.PHONY: dashboard-build dashboard-lint dashboard-check
dashboard-build:
	$(MAKE) -f scripts/dashboard/Makefile build

dashboard-lint:
	$(MAKE) -f scripts/dashboard/Makefile lint

dashboard-check:
	$(MAKE) -f scripts/dashboard/Makefile check
