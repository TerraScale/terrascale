.PHONY: check contracts

check:
	test -z "$$(gofmt -l .)"
	go vet ./...
	go test -race ./...

contracts:
	@echo "contracts unavailable: terrabase has not published a versioned contract artifact yet" >&2
	@false
