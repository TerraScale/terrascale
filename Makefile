.PHONY: check contracts

CONTRACT_VERSION ?= v1
# CONTRACT_SOURCE may point at a local terrabase/contracts producer artifact.
# CONTRACT_URL is an immutable registry package URL when v1 is published.

check:
	test -z "$$(gofmt -l .)"
	python3 scripts/verify_contracts.py
	python3 scripts/contracts_test.py
	go vet ./...
	go test -race ./...

contracts:
	@CONTRACT_VERSION="$(CONTRACT_VERSION)" python3 scripts/contracts.py --version "$(CONTRACT_VERSION)"
