.PHONY: check contracts

CONTRACT_VERSION ?= $(shell sed -e 's/[[:space:]]//g' CONTRACT_VERSION)
# CONTRACT_URL is an immutable terrabase package URL and is required by
# default/CI. CONTRACT_SOURCE is an explicit pre-publication escape hatch.

check:
	test -z "$$(gofmt -l .)"
	python3 scripts/verify_contracts.py
	python3 scripts/contracts_test.py
	go vet ./...
	go test -race ./...

contracts:
	@CONTRACT_VERSION="$(CONTRACT_VERSION)" CONTRACT_URL="$(CONTRACT_URL)" CONTRACT_SOURCE="$(CONTRACT_SOURCE)" python3 scripts/contracts.py --version "$(CONTRACT_VERSION)"
