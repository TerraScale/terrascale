DOTNET ?= dotnet
NPM ?= npm
CONFIGURATION ?= Release
SOLUTION = TerraScale.Dashboard.slnx
E2E = $(DOTNET) run --configuration $(CONFIGURATION) --project tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj --
RESULTS = --report-trx --results-directory "$(CURDIR)/test-results/tunit"

.PHONY: build lint css check-css js dev demo test-e2e test-fast test-slow test-browser test-hot-reload test-live-readiness perf check-e2e-shards

build:
	$(DOTNET) build $(SOLUTION) --configuration $(CONFIGURATION) -p:RestoreLockedMode=true

lint: check-css
	python3 scripts/verify-sdk.py
	python3 scripts/verify-e2e.py
	$(DOTNET) format $(SOLUTION) --verify-no-changes --no-restore

css:
	$(NPM) run build:css

# Tests serve the committed stylesheet, so it must match web/assets.
check-css:
	$(NPM) run build:css -- --check

js:
	$(NPM) run build:js

dev:
	scripts/dev.sh

demo:
	scripts/demo.sh

# Every dashboard test target runs the TUnit application E2E journeys.
test-e2e:
	$(E2E) $(RESULTS)

test-fast test-slow test-browser: test-e2e

test-hot-reload:
	$(E2E) --treenode-filter '/*/*/HotReloadJourneyTests/*' $(RESULTS) --report-trx-filename hot-reload.trx

test-live-readiness:
	$(E2E) --treenode-filter '/*/*/LiveProductionReadinessJourneyTests/*' $(RESULTS) --report-trx-filename live-production-readiness.trx

perf:
	$(E2E) --treenode-filter '/*/*/PerformanceJourneyTests/*' $(RESULTS) --report-trx-filename performance.trx

check-e2e-shards:
	$(E2E) --list-tests
