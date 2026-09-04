.PHONY: check contracts

# check is the TypeScript SDK verification set from the workspace GOAL.
check: contracts
	git diff --exit-code
	npm ci
	npm run typecheck
	npm run build
	npm test

# Regenerate the SDK's committed route and OpenAPI contract descriptors.
contracts:
	npm run generate:route-manifest
	npm run generate:openapi-contract-descriptor
