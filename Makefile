GO ?= go
NPM ?= npm
PORT ?= 5481
GOFLAGS ?= -mod=vendor
VERSION ?= $(shell git rev-parse HEAD 2>/dev/null || echo dev)
export GOFLAGS

.PHONY: build check-css check-generated check-modules check-e2e-shards css dev generate js serve lint perf test-fast test-slow test-browser

# Migration checks are explicit until the full Go feature surface is ported.
.PHONY: dotnet-build dotnet-lint dotnet-test-fast dotnet-test-e2e dotnet-dev dotnet-demo dotnet-parity-reference dotnet-test-browser dotnet-parity dotnet-test-hot-reload dotnet-test-live-readiness
dotnet-build:
	dotnet build TerraScale.Dashboard.slnx --configuration Release -p:RestoreLockedMode=true

dotnet-lint:
	python3 scripts/dotnet/verify-sdk.py
	python3 scripts/dotnet/verify-e2e.py
	dotnet format TerraScale.Dashboard.slnx --verify-no-changes --no-restore
	dotnet format tests/dotnet/e2e/TerraScale.Dashboard.E2E.csproj --verify-no-changes --no-restore

dotnet-test-fast: dotnet-test-e2e

dotnet-test-e2e:
	dotnet run --configuration Release --project tests/dotnet/e2e/TerraScale.Dashboard.E2E.csproj -- --treenode-filter '/*/*/*/*[Category!=ReferenceCapture]' --report-trx --results-directory "$(CURDIR)/test-results/tunit"

dotnet-test-browser: dotnet-test-e2e

dotnet-parity:
	dotnet run --configuration Release --project tests/dotnet/e2e/TerraScale.Dashboard.E2E.csproj -- --treenode-filter '/*/*/VisualParityJourneyTests/*' --report-trx --report-trx-filename visual-parity.trx --results-directory "$(CURDIR)/test-results/tunit"

dotnet-test-hot-reload:
	dotnet run --configuration Release --project tests/dotnet/e2e/TerraScale.Dashboard.E2E.csproj -- --treenode-filter '/*/*/HotReloadJourneyTests/*' --report-trx --report-trx-filename hot-reload.trx --results-directory "$(CURDIR)/test-results/tunit"

dotnet-test-live-readiness:
	dotnet run --configuration Release --project tests/dotnet/e2e/TerraScale.Dashboard.E2E.csproj -- --treenode-filter '/*/*/LiveProductionReadinessJourneyTests/*' --report-trx --report-trx-filename live-production-readiness.trx --results-directory "$(CURDIR)/test-results/tunit"

dotnet-dev:
	scripts/dotnet/dev.sh

dotnet-demo:
	scripts/dotnet/demo.sh

dotnet-parity-reference: js
	dotnet run --configuration Release --project tests/dotnet/e2e/TerraScale.Dashboard.E2E.csproj -- --treenode-filter '/*/*/GoReferenceCaptureJourneyTests/*' --report-trx --report-trx-filename go-reference-capture.trx --results-directory "$(CURDIR)/test-results/tunit"

TEMPL_GENERATE_ARGS = -path internal -include-version=false -include-timestamp=false

generate:
	GOTOOLCHAIN=local GOFLAGS=-mod=vendor $(GO) tool templ generate $(TEMPL_GENERATE_ARGS)

check-generated:
	GOTOOLCHAIN=local GOFLAGS=-mod=vendor $(GO) tool templ generate $(TEMPL_GENERATE_ARGS) -check

# Check that dependencies remain available publicly even though builds use vendor.
# This includes the public ts-go replacement; Forgejo's go-import metadata points
# to its own Git repository. Public proxy/sumdb availability is a CI requirement.
# Disable saved Go settings and clear inherited privacy/insecure exclusions.
check-modules:
	@set -eu; \
		task_module_cache=$$(mktemp -d); \
		trap 'task_exit_status=$$?; if [ -n "$${task_module_cache:-}" ]; then chmod -R u+w -- "$$task_module_cache" && rm -rf -- "$$task_module_cache" || :; fi; exit "$$task_exit_status"' EXIT; \
		trap 'exit 1' HUP INT TERM; \
		GOENV=off GOTOOLCHAIN=local GOMODCACHE="$$task_module_cache" GOFLAGS=-mod=readonly \
		GOPROXY=https://proxy.golang.org,direct GOPRIVATE= GONOPROXY= \
		GONOSUMDB= GOINSECURE= GOSUMDB=sum.golang.org $(GO) mod download all

css:
	$(NPM) run build:css

# Tests serve the committed stylesheet while the image bundles web/assets, so
# the two must not drift.
check-css:
	$(NPM) run build:css -- --check

# Browser scripts are generated (web/static/js is not committed) and embedded,
# so every Go build, vet, and test needs them first.
js:
	$(NPM) run build:js

build: css js check-generated
	mkdir -p bin
	CGO_ENABLED=0 $(GO) build -trimpath -buildvcs=false \
		-ldflags='-s -w -X terrascale.tech/dashboard/internal/platform/buildinfo.version=$(VERSION)' \
		-o bin/dashboard-release ./cmd/dashboard

dev:
	PORT=$(PORT) scripts/dev.sh

serve: js
	TS_DASHBOARD_PRODUCTION_DEMO=1 DASHBOARD_ADDR=0.0.0.0:$(PORT) $(GO) run ./cmd/dashboard

lint: js check-generated
	@test -z "$$(find cmd internal tests web -type f -name '*.go' -print0 | xargs -0 gofmt -l)"
	$(GO) vet ./...

# Every dashboard test target runs application E2E journeys under TUnit.
# Original Go browser fixtures remain available only for migration reference
# capture; the application is not cut over to C# until qualification passes.
test-fast test-slow test-browser: dotnet-test-e2e

# Compatibility discovery target: the C# suite has one TUnit inventory.
check-e2e-shards:
	dotnet run --configuration Release --project tests/dotnet/e2e/TerraScale.Dashboard.E2E.csproj -- --list-tests --treenode-filter '/*/*/*/*[Category!=ReferenceCapture]'

perf:
	dotnet run --configuration Release --project tests/dotnet/e2e/TerraScale.Dashboard.E2E.csproj -- --treenode-filter '/*/*/PerformanceJourneyTests/*' --report-trx --report-trx-filename performance.trx --results-directory "$(CURDIR)/test-results/tunit"
