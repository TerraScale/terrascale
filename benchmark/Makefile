.DEFAULT_GOAL := help
.PHONY: help build build-go build-csharp build-rust check fmt fmt-check run

TERRABASE_BIN ?=
AUTH_DLL ?=
DASHBOARD_DLL ?=
CSHARP_BIN ?= $(CURDIR)/csharp/bin/Release/net11.0/TerraScale.Benchmark.dll
RUST_BIN ?= $(CURDIR)/rust/target/release/ts-bench-rust
BENCH_ARGS ?=

help:
	@printf '%s\n' \
		'build: build Go orchestrator plus C# and Rust SDK runners' \
		'check: format, vet, lint, clippy and Release build for Go, C#, and Rust' \
		'run: boot local TerraBase + ts-auth + Dashboard and run C#, Go, and Rust E2E benchmarks'

build: build-go build-csharp build-rust

build-go:
	go build -trimpath -o bin/ts-bench ./go/cmd/ts-bench

build-csharp:
	dotnet build csharp/TerraScale.Benchmark.csproj -c Release

build-rust:
	cargo build --manifest-path rust/Cargo.toml --release --locked

fmt:
	gofmt -w go
	cargo fmt --manifest-path rust/Cargo.toml

fmt-check:
	@test -z "$$(gofmt -l go)" || { gofmt -l go; exit 1; }
	dotnet format whitespace csharp/TerraScale.Benchmark.csproj --verify-no-changes
	cargo fmt --manifest-path rust/Cargo.toml --check

check: fmt-check
	go vet ./...
	$(MAKE) build-go
	$(MAKE) build-csharp
	cargo clippy --manifest-path rust/Cargo.toml --locked --all-targets -- -D warnings

run: build
	bin/ts-bench \
		$(if $(TERRABASE_BIN),--terrabase-bin $(TERRABASE_BIN)) \
		$(if $(AUTH_DLL),--auth-dll $(AUTH_DLL)) \
		$(if $(DASHBOARD_DLL),--dashboard-dll $(DASHBOARD_DLL)) \
		--csharp-bin $(CSHARP_BIN) \
		--rust-bin $(RUST_BIN) \
		$(BENCH_ARGS)
