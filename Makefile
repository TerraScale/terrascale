SOLUTION := TerraScale.slnx
CONFIGURATION ?= Debug
RUNTIME ?= linux-x64
PUBLISH_DIR ?= artifacts/publish
TS_AUTH_REVISION ?=
export TS_AUTH_REVISION
DOTNET ?= dotnet
NPM ?= npm
TERRABASE_BINARY ?= $(CURDIR)/src/TerraBase/bin/$(CONFIGURATION)/net11.0/terrabase
REDOCLY := npx --yes @redocly/cli@2.57.0

VENDORED_UPSTREAM := src/vendor/Turso.Data.Sqlite/src src/vendor/SqlParserCS/SqlParser
TURSO_NATIVE_DIR := src/vendor/Turso.Data.Sqlite/native

AUTH_HOST_DLL := src/TerraScale.Authentication/bin/$(CONFIGURATION)/net11.0/ts-auth.dll
AUTH_PROJECT := src/TerraScale.Authentication.EntityFrameworkDesign/TerraScale.Authentication.EntityFrameworkDesign.csproj
AUTH_FORMAT_PROJECTS := src/TerraScale.Authentication/TerraScale.Authentication.csproj \
	src/TerraScale.Authentication.Contracts/TerraScale.Authentication.Contracts.csproj \
	src/TerraScale.Authentication.Security/TerraScale.Authentication.Security.csproj \
	src/TerraScale.Authentication.EntityFrameworkDesign/TerraScale.Authentication.EntityFrameworkDesign.csproj

TERRABASE_PROJECT := src/TerraBase/TerraBase.csproj
TERRABASE_FORMAT_PROJECTS := src/TerraBase/TerraBase.csproj \
	src/TerraScale.TerraBase.Contracts/TerraScale.TerraBase.Contracts.csproj \
	src/TerraScale.TerraBase.Rpc/TerraScale.TerraBase.Rpc.csproj

DASHBOARD_PROJECT := src/TerraScale.Dashboard.EntityFrameworkDesign/TerraScale.Dashboard.EntityFrameworkDesign.csproj
DASHBOARD_HOST_DLL := src/TerraScale.Dashboard/bin/$(CONFIGURATION)/net11.0/TerraScale.Dashboard.dll
DASHBOARD_WEB := src/TerraScale.Dashboard/web

ASPIRE_PROJECT := src/TerraScale.Aspire/TerraScale.Aspire.csproj

LIBS_STYLE_PROJECTS := src/TerraScale.Analyzers/TerraScale.Analyzers.csproj \
	src/TerraScale.Endpoints/TerraScale.Endpoints.csproj \
	src/TerraScale.Endpoints.Generators/TerraScale.Endpoints.Generators.csproj \
	src/TerraScale.Endpoints.Example/TerraScale.Endpoints.Example.csproj \
	src/TerraScale.Logging/TerraScale.Logging.csproj
LIBS_BUILD_PROJECTS := src/TerraScale.Analyzers/TerraScale.Analyzers.csproj \
	src/TerraScale.Endpoints.Example/TerraScale.Endpoints.Example.csproj \
	src/TerraScale.Logging/TerraScale.Logging.csproj \
	benchmark/dotnet/TerraScale.Endpoints.Benchmarks/TerraScale.Endpoints.Benchmarks.csproj \
	benchmark/dotnet/TerraScale.Logging.Benchmarks/TerraScale.Logging.Benchmarks.csproj
LIBS_FORMAT_PROJECTS := $(LIBS_STYLE_PROJECTS) \
	benchmark/dotnet/TerraScale.Endpoints.Benchmarks/TerraScale.Endpoints.Benchmarks.csproj \
	benchmark/dotnet/TerraScale.Logging.Benchmarks/TerraScale.Logging.Benchmarks.csproj

BENCHMARK_DOTNET_PROJECTS := benchmark/dotnet/TerraScale.Endpoints.Benchmarks/TerraScale.Endpoints.Benchmarks.csproj \
	benchmark/dotnet/TerraScale.EntityFrameworkCore.Benchmarks/TerraScale.EntityFrameworkCore.Benchmarks.csproj \
	benchmark/dotnet/TerraScale.Logging.Benchmarks/TerraScale.Logging.Benchmarks.csproj

.PHONY: restore turso-native build fmt fmt-check lint check clean dev \
	libs-restore libs-fmt-check libs-build libs-check libs-nativeaot-check \
	auth-restore auth-fmt-check auth-build auth-audit auth-openapi-check auth-check auth-publish \
	terrabase-restore terrabase-fmt-check terrabase-build terrabase-run terrabase-publish \
	dashboard-restore dashboard-build dashboard-lint dashboard-security dashboard-check dashboard-css dashboard-check-css dashboard-js \
	aspire-restore aspire-fmt-check aspire-build \
	benchmark-build benchmark-fmt-check benchmark-check bench \
	docs-build landing-build status-build \
	sdk-check sdk-dotnet-check sdk-js-check sdk-go-check sdk-python-check sdk-rust-check

restore:
	$(DOTNET) restore $(SOLUTION) --locked-mode

turso-native:
	src/vendor/Turso.Native/scripts/verify-turso-native.sh $(TURSO_NATIVE_DIR) >/dev/null 2>&1 || src/vendor/Turso.Native/scripts/download-turso-native.sh $(TURSO_NATIVE_DIR)

build: restore turso-native
	$(DOTNET) build $(SOLUTION) --configuration $(CONFIGURATION) --no-restore

fmt:
	$(DOTNET) format $(SOLUTION) --exclude $(VENDORED_UPSTREAM)

fmt-check: restore
	Configuration=$(CONFIGURATION) $(DOTNET) format whitespace $(SOLUTION) --verify-no-changes --no-restore --exclude $(VENDORED_UPSTREAM)

lint: fmt-check build

check: lint libs-check auth-check dashboard-check benchmark-check

clean:
	$(DOTNET) clean $(SOLUTION) --configuration $(CONFIGURATION)
	rm -rf artifacts

dev: turso-native
	$(DOTNET) tool restore
	$(DOTNET) aspire run --project $(ASPIRE_PROJECT)

libs-restore:
	set -e; for p in $(LIBS_BUILD_PROJECTS); do $(DOTNET) restore $$p --locked-mode; done

libs-fmt-check: libs-restore
	set -e; for p in $(LIBS_FORMAT_PROJECTS); do Configuration=$(CONFIGURATION) $(DOTNET) format whitespace $$p --verify-no-changes --no-restore; done

libs-build: libs-restore
	set -e; for p in $(LIBS_BUILD_PROJECTS); do $(DOTNET) build $$p --configuration $(CONFIGURATION) --no-restore; done

libs-check: libs-build
	set -e; for p in $(LIBS_STYLE_PROJECTS); do Configuration=$(CONFIGURATION) $(DOTNET) format style $$p --verify-no-changes --no-restore; done

libs-nativeaot-check: libs-restore
	$(DOTNET) publish src/TerraScale.Endpoints.Example/TerraScale.Endpoints.Example.csproj --configuration Release --runtime $(RUNTIME) --output artifacts/nativeaot/endpoints-example

auth-restore:
	$(DOTNET) restore $(AUTH_PROJECT) --locked-mode

auth-fmt-check: auth-restore
	set -e; for p in $(AUTH_FORMAT_PROJECTS); do Configuration=$(CONFIGURATION) $(DOTNET) format whitespace $$p --verify-no-changes --no-restore; done

auth-build: auth-restore
	$(DOTNET) build $(AUTH_PROJECT) --configuration $(CONFIGURATION) --no-restore --nologo -m

auth-audit: auth-restore
	$(DOTNET) run sdk/dotnet/scripts/check-nuget-vulnerabilities.cs src/TerraScale.Authentication/TerraScale.Authentication.csproj

auth-openapi-check: auth-build
	mkdir -p artifacts
	.github/scripts/auth/export-openapi.sh $(AUTH_HOST_DLL) artifacts/openapi.json
	$(REDOCLY) lint artifacts/openapi.json src/TerraScale.Authentication/platform-api.openapi.yaml

auth-check: auth-audit auth-openapi-check

auth-publish:
	$(DOTNET) publish src/TerraScale.Authentication --configuration Release --runtime $(RUNTIME) --self-contained --output "$(PUBLISH_DIR)"

terrabase-restore:
	$(DOTNET) restore $(TERRABASE_PROJECT) --locked-mode

terrabase-fmt-check: terrabase-restore
	set -e; for p in $(TERRABASE_FORMAT_PROJECTS); do Configuration=$(CONFIGURATION) $(DOTNET) format whitespace $$p --verify-no-changes --no-restore; done

terrabase-build: terrabase-restore turso-native
	$(DOTNET) build $(TERRABASE_PROJECT) --configuration $(CONFIGURATION) --no-restore

terrabase-run: turso-native
	$(DOTNET) run --project src/TerraBase --configuration $(CONFIGURATION)

terrabase-publish: turso-native
	$(DOTNET) publish src/TerraBase --configuration Release --runtime $(RUNTIME) --self-contained --output "$(PUBLISH_DIR)"
	test ! -e "$(PUBLISH_DIR)/libturso_sdk_kit.so"
	test ! -e "$(PUBLISH_DIR)/libterrascale_sql_parser.so"

dashboard-restore:
	$(DOTNET) restore $(DASHBOARD_PROJECT) --locked-mode

dashboard-build: dashboard-restore
	$(DOTNET) build $(DASHBOARD_PROJECT) --configuration $(CONFIGURATION) --no-restore

dashboard-css:
	$(NPM) --prefix $(DASHBOARD_WEB) run build:css

dashboard-check-css:
	$(NPM) --prefix $(DASHBOARD_WEB) run check:css

dashboard-js:
	$(NPM) --prefix $(DASHBOARD_WEB) run build:js

dashboard-lint: dashboard-check-css
	python3 .github/scripts/dashboard/check-simple-ui.py
	python3 .github/scripts/dashboard/verify-sdk.py
	Configuration=$(CONFIGURATION) DOTNET="$(DOTNET)" bash .github/scripts/dashboard/format.sh

dashboard-security:
	$(DOTNET) restore $(DASHBOARD_PROJECT) --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low
	$(NPM) --prefix $(DASHBOARD_WEB) audit --audit-level=low

dashboard-check: dashboard-build dashboard-lint dashboard-security

aspire-restore:
	$(DOTNET) restore $(ASPIRE_PROJECT) --locked-mode

aspire-fmt-check: aspire-restore
	Configuration=$(CONFIGURATION) $(DOTNET) format whitespace $(ASPIRE_PROJECT) --verify-no-changes --no-restore

aspire-build: aspire-restore
	$(DOTNET) build $(ASPIRE_PROJECT) --configuration $(CONFIGURATION) --no-restore

benchmark-build: restore
	$(MAKE) -C benchmark build
	set -e; for p in $(BENCHMARK_DOTNET_PROJECTS); do $(DOTNET) build $$p --configuration $(CONFIGURATION) --no-restore; done

benchmark-fmt-check:
	$(MAKE) -C benchmark fmt-check

benchmark-check:
	$(MAKE) -C benchmark check

bench: terrabase-build auth-build dashboard-build
	$(MAKE) -C benchmark run TERRABASE_BIN=$(TERRABASE_BINARY) AUTH_DLL=$(CURDIR)/$(AUTH_HOST_DLL) DASHBOARD_DLL=$(CURDIR)/$(DASHBOARD_HOST_DLL)

docs-build:
	cd sites/docs && npm ci --no-audit --no-fund && node scripts/generate-pricing.mjs --check && node scripts/generate-llms.mjs --check \
		&& node scripts/i18n-sync.mjs --check && npm run build

landing-build:
	cd sites/landing && npm ci --no-audit --no-fund && npm run build

status-build:
	cd sites/status && npm ci --no-audit --no-fund && npm run check && npm run lint && npm run build

sdk-check: sdk-dotnet-check sdk-js-check sdk-go-check sdk-python-check sdk-rust-check

sdk-dotnet-check:
	cd sdk/dotnet && $(DOTNET) restore TerraScale.DotNet.slnx --locked-mode
	cd sdk/dotnet && $(DOTNET) build TerraScale.DotNet.slnx --configuration Release --no-restore

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
