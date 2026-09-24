GO ?= go
NPM ?= npm
PORT ?= 5481
GOFLAGS ?= -mod=vendor
export GOFLAGS

.PHONY: build css dev serve lint test-fast test-slow

css:
	$(NPM) run build:css

build: css
	mkdir -p bin
	CGO_ENABLED=0 $(GO) build -trimpath -buildvcs=false -ldflags='-s -w' -o bin/dashboard-release ./cmd/dashboard

dev:
	PORT=$(PORT) scripts/dev.sh

serve:
	TS_DASHBOARD_PRODUCTION_DEMO=1 DASHBOARD_ADDR=0.0.0.0:$(PORT) $(GO) run ./cmd/dashboard

lint:
	@test -z "$$(find cmd internal tests/integration -type f -name '*.go' -print0 | xargs -0 gofmt -l)"
	$(GO) vet ./...

test-fast:
	$(GO) test -race -count=1 ./...

test-slow:
	scripts/test-slow.sh
