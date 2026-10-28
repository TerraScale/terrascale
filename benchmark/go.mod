module terrascale.tech/ts-benchmark

go 1.27.3

require (
	github.com/HdrHistogram/hdrhistogram-go v1.1.2
	github.com/oklog/ulid/v2 v2.1.2
	terrascale.tech/sdk v0.0.0-20261028013942-07ffb51e1d66
)

require github.com/stretchr/testify v1.11.1 // indirect

// Current public SDK: pooled HTTP/2 and the project-scoped ULID document contract.
replace terrascale.tech/sdk => github.com/TerraScale/ts-go v0.0.0-20261028013942-07ffb51e1d66
