DOTNET ?= dotnet
NPM ?= npm
# The asset build's npm project.
WEB = src/TerraScale.Dashboard/web
SOLUTION = TerraScale.Dashboard.slnx
E2E = $(DOTNET) run --configuration Release --project tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj --
RESULTS = --report-trx --results-directory "$(CURDIR)/test-results/tunit"

.PHONY: build lint css check-css js dev demo test-e2e test-fast test-slow test-hot-reload test-live-readiness perf check-e2e-shards

build:
	$(DOTNET) build $(SOLUTION) --configuration Release -p:RestoreLockedMode=true

lint: check-css
	python3 scripts/verify-sdk.py
	python3 scripts/verify-e2e.py
	$(DOTNET) format $(SOLUTION) --verify-no-changes --no-restore

css:
	$(NPM) --prefix $(WEB) run build:css

# Tests serve the committed stylesheet, so it must match web/assets.
check-css:
	$(NPM) --prefix $(WEB) run check:css

js:
	$(NPM) --prefix $(WEB) run build:js

dev:
	scripts/dev.sh

demo:
	scripts/demo.sh

# Every dashboard test target runs TUnit application E2E journeys against real Kestrel hosts with
# test-only providers (tests/TerraScale.Dashboard.E2E/Demo). test-fast is the HTTP, MCP, CLI and
# customer-SDK journeys; test-slow is the browser journeys and the internal-billing policy suites
# ([Category("Slow")]); test-e2e is both and is the merge/release gate. Explicit journeys (hot reload,
# performance, live readiness) run only through their own targets below.
test-e2e:
	$(E2E) $(RESULTS)

test-fast:
	$(E2E) --treenode-filter '/*/*/*/*[Category!=Slow]' $(RESULTS) --report-trx-filename fast.trx

test-slow:
	$(E2E) --treenode-filter '/*/*/*/*[Category=Slow]' $(RESULTS) --report-trx-filename slow.trx

test-hot-reload:
	$(E2E) --treenode-filter '/*/*/HotReloadJourneyTests/*' $(RESULTS) --report-trx-filename hot-reload.trx

test-live-readiness:
	$(E2E) --treenode-filter '/*/*/LiveProductionReadinessJourneyTests/*' $(RESULTS) --report-trx-filename live-production-readiness.trx

perf:
	$(E2E) --treenode-filter '/*/*/PerformanceJourneyTests/*' $(RESULTS) --report-trx-filename performance.trx

check-e2e-shards:
	$(E2E) --list-tests
