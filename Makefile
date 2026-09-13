.PHONY: check build test qualify-remote contracts
check:
	npm ci && npm run build && npm test
build:
	npm run build
test:
	npm test
qualify-remote:
	@test -n "$(TERRASCALE_SANDBOX_URL)" || (echo "TERRASCALE_SANDBOX_URL is required for remote qualification" >&2; exit 1)
	@test -n "$(TERRASCALE_SANDBOX_API_KEY)" || (echo "TERRASCALE_SANDBOX_API_KEY is required for remote qualification" >&2; exit 1)
	@test -n "$(TERRASCALE_SANDBOX_DATABASE_ID)" || (echo "TERRASCALE_SANDBOX_DATABASE_ID is required for remote qualification" >&2; exit 1)
	npm ci && npm run build && npm run test:remote
contracts:
	@echo "ts-docs has no generated contracts (no CONTRACT_VERSION pin yet; see P-1)."
