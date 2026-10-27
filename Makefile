DOTNET ?= dotnet
NPM ?= npm
# The asset build's npm project.
WEB = src/TerraScale.Dashboard/web
SOLUTION = TerraScale.Dashboard.slnx
E2E = $(DOTNET) run --configuration Release --project tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj --
RESULTS = --report-trx --results-directory "$(CURDIR)/test-results/tunit"

.PHONY: build lint security check css check-css js dev demo test-e2e test-fast test-slow test-hot-reload test-live-readiness perf check-e2e-shards

build:
	$(DOTNET) build $(SOLUTION) --configuration Release -p:RestoreLockedMode=true

lint: check-css
	python3 scripts/check-design-classes.py
	python3 scripts/verify-sdk.py
	python3 scripts/verify-e2e.py
	python3 scripts/report-slow-failure.py --self-check
	node scripts/check-document-changes.mjs
	DOTNET="$(DOTNET)" bash scripts/ci/format.sh

security:
	$(DOTNET) restore $(SOLUTION) --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low
	$(NPM) --prefix $(WEB) audit --audit-level=low

# Required before advancing an artifact. Slow runs independently and retains failures.
check: build lint security test-fast

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
# ([Category("Slow")]); test-e2e is both for local investigation. Fast is the merge/release gate.
# Slow is non-blocking and failures are reported by its independent workflow. Explicit journeys (hot reload,
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
