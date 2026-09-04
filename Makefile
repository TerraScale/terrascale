.PHONY: check build test contracts
check:
	npm ci && npm run build && npm test
build:
	npm run build
test:
	npm test
contracts:
	@echo "ts-docs has no generated contracts (no CONTRACT_VERSION pin yet; see P-1)."
