DOTNET ?= dotnet
NPM ?= npm
# The asset build's npm project.
WEB = src/TerraScale.Dashboard/web
SOLUTION = scripts/dashboard/TerraScale.Dashboard.slnx

.PHONY: build lint security check css check-css js dev

build:
	$(DOTNET) build $(SOLUTION) --configuration Release -p:RestoreLockedMode=true

lint: check-css
	python3 scripts/dashboard/check-simple-ui.py
	python3 scripts/dashboard/verify-sdk.py
	DOTNET="$(DOTNET)" bash scripts/dashboard/ci/format.sh

security:
	$(DOTNET) restore $(SOLUTION) --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low
	$(NPM) --prefix $(WEB) audit --audit-level=low

check: build lint security

css:
	$(NPM) --prefix $(WEB) run build:css

check-css:
	$(NPM) --prefix $(WEB) run check:css

js:
	$(NPM) --prefix $(WEB) run build:js

dev:
	scripts/dashboard/dev.sh
