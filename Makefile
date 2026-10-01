GO ?= go
NPM ?= npm
PORT ?= 5481
GOFLAGS ?= -mod=vendor
GO_TEST_FLAGS ?= -race -count=1
VERSION ?= $(shell git rev-parse HEAD 2>/dev/null || echo dev)
export GOFLAGS

.PHONY: build check-css check-generated check-modules check-e2e-shards css dev generate js serve lint perf test-fast test-slow test-unit test-browser

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

# Package unit tests plus the browser suites, which drive Chromium through
# playwright-go against an in-process dashboard, or DASHBOARD_E2E_BASE_URL
# when set. Browser tests initialize Monaco and exercise dialogs; running
# several at once can starve Chromium and time out otherwise-stable actions.
# Serial browser runs take about 6–7 minutes, within CI's 30-minute budget;
# keep internal unit tests parallel and fail fast if they fail. -p 1 prevents
# future browser test packages from running alongside each other as well.
test-fast: js
	$(GO) test $(GO_TEST_FLAGS) -short -parallel 8 ./internal/...
	$(GO) test $(GO_TEST_FLAGS) -short -p 1 -parallel 1 ./tests/...

test-slow: test-unit test-browser

# test-slow in parts, so CI runs the unit tests and the browser shards as
# parallel jobs. E2E_SHARD=INDEX/TOTAL runs one round-robin share of the
# browser tests in its own process, dashboard and Chromium (see
# scripts/e2e-shard.sh); the default 1/1 runs them all.
E2E_SHARD ?= 1/1
E2E_SHARDS ?= $(shell scripts/e2e-shard-count.sh)

check-e2e-shards: js
	@GO="$(GO)" scripts/check-e2e-shards.sh "$(E2E_SHARDS)" $(GO_TEST_FLAGS)

test-unit: js
	$(GO) test $(GO_TEST_FLAGS) -parallel 8 ./internal/...

test-browser: js
	@run=$$(GO="$(GO)" scripts/e2e-shard.sh "$(E2E_SHARD)" $(GO_TEST_FLAGS)) && \
		printf 'test-browser %s: -run %s\n' "$(E2E_SHARD)" "$$run" >&2 && \
		$(GO) test $(GO_TEST_FLAGS) -p 1 -parallel 1 -run "$$run" ./tests/...

perf: js
	DASHBOARD_E2E_PERF=1 $(GO) test -count=1 -run TestPerformance -v ./tests/e2e
