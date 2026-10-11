module terrascale.tech/ts-benchmark

go 1.25.0

require (
	github.com/HdrHistogram/hdrhistogram-go v1.1.2
	github.com/oklog/ulid/v2 v2.1.2
	terrascale.tech/sdk v0.0.0-20261011145515-9f704645aa3a
)

require github.com/stretchr/testify v1.11.1 // indirect

// TS-832: ts-go main with Database.Delete (#43).
replace terrascale.tech/sdk => git.terrascale.tech/TerraScale/ts-go v0.0.0-20261011145515-9f704645aa3a
