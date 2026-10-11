.DEFAULT_GOAL := help
.PHONY: help build test test-fast fast check fmt fmt-check smoke api-bench-build api-bench
help:
	@printf '%s\n' 'build: build the local protocol runner' 'test: workload, scheduler, HDR and aggregation tests with the race detector' 'smoke: runner end-to-end tests against local fake clients' 'check: formatting, vet, build and tests (no production calls)' 'api-bench-build: build the TS-832 API benchmark runner and its Go and .NET clients' 'api-bench: run the API benchmark against production (CLUSTER_STATE=stable|changing)'
build:
	go build -trimpath -o bin/bench-runner ./cmd/bench-runner
api-bench-build:
	go build -trimpath -o bin/api-bench ./cmd/api-bench
	go build -trimpath -o bin/api-bench-go ./clients/go
	$(MAKE) -C clients/dotnet api-bench-publish
api-bench: api-bench-build
	@test -n "$(CLUSTER_STATE)" || { echo 'CLUSTER_STATE=stable or CLUSTER_STATE=changing is required'; exit 1; }
	bin/api-bench --cluster-state $(CLUSTER_STATE)
fmt:
	gofmt -w cmd internal
fmt-check:
	@test -z "$$(gofmt -l cmd internal)" || { gofmt -l cmd internal; exit 1; }
# The runner end-to-end tests drive bench-runner against local fake clients: no production calls.
smoke:
	go test -race -count=1 ./cmd/bench-runner
test test-fast:
	go test -race ./...
check fast:
	@test -z "$$(gofmt -l cmd internal)" || { gofmt -l cmd internal; exit 1; }
	go vet ./...
	$(MAKE) build
	$(MAKE) test
