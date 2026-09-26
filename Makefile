GO ?= go
NPM ?= npm
PORT ?= 5481
GOFLAGS ?= -mod=vendor
VERSION ?= $(shell git rev-parse HEAD 2>/dev/null || echo dev)
export GOFLAGS

.PHONY: build check-generated css dev generate js serve lint perf test-fast test-slow

TEMPL_GENERATE_ARGS = -path internal -include-version=false -include-timestamp=false

generate:
	GOTOOLCHAIN=local GOFLAGS=-mod=vendor $(GO) tool templ generate $(TEMPL_GENERATE_ARGS)

check-generated:
	GOTOOLCHAIN=local GOFLAGS=-mod=vendor $(GO) tool templ generate $(TEMPL_GENERATE_ARGS) -check

css:
	$(NPM) run build:css

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
# when set.
test-fast: js
	$(GO) test -race -short -count=1 -parallel 8 ./internal/... ./tests/...

test-slow: js
	$(GO) test -race -count=1 -parallel 8 ./internal/... ./tests/...

perf: js
	DASHBOARD_E2E_PERF=1 $(GO) test -count=1 -run TestPerformance -v ./tests/e2e
