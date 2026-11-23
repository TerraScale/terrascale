module terrascale.tech/ts-benchmark

go 1.27

require (
	github.com/oklog/ulid/v2 v2.1.2
	terrascale.tech/sdk v0.0.0
)

require github.com/coder/websocket v1.8.15 // indirect

replace terrascale.tech/sdk => ../sdk/go
