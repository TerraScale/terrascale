.DEFAULT_GOAL := help
.PHONY: help build test test-fast fast check fmt
help:
	@printf '%s\n' 'build: build the local protocol runner' 'test: workload, scheduler, HDR and aggregation tests with the race detector' 'check: formatting, vet, build and tests (no production calls)'
build:
	go build -trimpath -o bin/bench-runner ./cmd/bench-runner
fmt:
	gofmt -w cmd internal
test test-fast:
	go test -race ./...
check fast:
	@test -z "$$(gofmt -l cmd internal)" || { gofmt -l cmd internal; exit 1; }
	go vet ./...
	$(MAKE) build
	$(MAKE) test
