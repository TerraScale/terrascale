GO ?= go
NPM ?= npm
PORT ?= 5481
GOFLAGS ?= -mod=vendor
export GOFLAGS

.PHONY: build check-generated css dev generate serve lint perf test-fast test-slow

TEMPL_GENERATE_ARGS = -path internal -include-version=false -include-timestamp=false

generate:
	GOTOOLCHAIN=local GOFLAGS=-mod=vendor $(GO) tool templ generate $(TEMPL_GENERATE_ARGS)

check-generated:
	GOTOOLCHAIN=local GOFLAGS=-mod=vendor $(GO) tool templ generate $(TEMPL_GENERATE_ARGS) -check

css:
	$(NPM) run build:css

build: css check-generated
	mkdir -p bin
	CGO_ENABLED=0 $(GO) build -trimpath -buildvcs=false -ldflags='-s -w' -o bin/dashboard-release ./cmd/dashboard

dev:
	PORT=$(PORT) scripts/dev.sh

serve:
	TS_DASHBOARD_PRODUCTION_DEMO=1 DASHBOARD_ADDR=0.0.0.0:$(PORT) $(GO) run ./cmd/dashboard

lint: check-generated
	@test -z "$$(find cmd internal tests web -type f -name '*.go' -print0 | xargs -0 gofmt -l)"
	$(GO) vet ./...

# Browser suites drive Chromium through playwright-go against an in-process
# dashboard, or DASHBOARD_E2E_BASE_URL when set.
test-fast:
	$(GO) test -race -short -count=1 -parallel 8 ./tests/...

test-slow:
	$(GO) test -race -count=1 -parallel 8 ./tests/...

perf:
	DASHBOARD_E2E_PERF=1 $(GO) test -count=1 -run TestPerformance -v ./tests/e2e
