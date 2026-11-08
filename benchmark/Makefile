.DEFAULT_GOAL := help
.PHONY: help build test test-fast fast check fmt fmt-check smoke insert-batch
help:
	@printf '%s\n' 'build: build the SQL replication tools' 'test-fast: free-read CPU guard checks and parallel Go checks' 'test: Fast checks' 'smoke: retained SQL-tool Fast checks' 'check: formatting, vet, build and Fast checks (no production calls)'
build:
	go build -trimpath -o bin/replica-lag ./cmd/replica-lag
	go build -trimpath -o bin/region-sim ./cmd/region-sim
	go build -trimpath -o bin/insert-bench ./cmd/insert-bench
fmt:
	gofmt -w cmd
fmt-check:
	@test -z "$$(gofmt -l cmd)" || { gofmt -l cmd; exit 1; }
test-fast:
	python3 -B scripts/free-reads-cpu-test.py
	go test -json -race -count=1 -timeout=30s ./...
smoke: test-fast
test: test-fast
fast: fmt-check build test-fast
check: fmt-check
	go vet ./...
	$(MAKE) build
	$(MAKE) test-fast

# Batched INSERT throughput against one local TerraBase node, over SQL REST and the Postgres wire.
TERRABASE_BINARY ?= $(CURDIR)/../src/TerraBase/bin/Debug/net11.0/terrabase
INSERT_BATCH_OUT ?= $(CURDIR)/bench/insert-batch-$(shell date -u +%Y-%m-%d).md
insert-batch:
	go build -trimpath -o bin/insert-bench ./cmd/insert-bench
	bin/insert-bench -terrabase $(TERRABASE_BINARY) -out $(INSERT_BATCH_OUT)
