.PHONY: check contracts

CONTRACT_VERSION := $(shell tr -d '[:space:]' < CONTRACT_VERSION 2>/dev/null)

# check is the TypeScript SDK verification set from the workspace GOAL.
check: contracts
	git diff --exit-code
	npm ci
	npm run typecheck
	npm run build
	npm test

# Regenerate generated route and OpenAPI metadata from the pinned terrabase
# bundle. The scripts require CONTRACT_SOURCE (local pre-publication) or the
# immutable CONTRACT_URL + CONTRACT_ARCHIVE_SHA256 registry settings; there is
# deliberately no Dashboard fallback.
contracts:
	@test -n "$(CONTRACT_VERSION)" || { echo "contracts: missing CONTRACT_VERSION" >&2; exit 1; }
	node scripts/generate-client-route-manifest.ts
	node scripts/generate-openapi-contract-descriptor.ts
