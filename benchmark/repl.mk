# Run from the repository root. Keep both revisions on the same TS_HEAVY_HOST.
PROFILE ?= loopback
N ?= 100000
DATABASES ?= 1
REPEATS ?= 3
SCENARIO ?= all
OUT ?= benchmark/bench/$(shell date -u +%F)-repl-baseline

.PHONY: bench-repl bench-repl-build bench-repl-run
bench-repl-build:
	$(MAKE) terrabase-build CONFIGURATION=Release
	cd benchmark && go build -trimpath -o bin/repl-local ./cmd/repl-local

bench-repl: bench-repl-build bench-repl-run

bench-repl-run:
	benchmark/bin/repl-local --profile "$(PROFILE)" --docs "$(N)" --databases "$(DATABASES)" --repeats "$(REPEATS)" --scenario "$(SCENARIO)" --out "$(OUT)"
