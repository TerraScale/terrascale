module terrascale.tech/ts-benchmark

go 1.27.3

require (
	github.com/HdrHistogram/hdrhistogram-go v1.1.2
	github.com/oklog/ulid/v2 v2.1.2
	terrascale.tech/sdk v0.0.0-20261028013942-07ffb51e1d66
)

require (
	github.com/jackc/pgpassfile v1.0.0 // indirect
	github.com/jackc/pgservicefile v0.0.0-20240606120523-5a60cdf6a761 // indirect
	github.com/jackc/pgx/v5 v5.9.2 // indirect
	golang.org/x/text v0.41.0 // indirect
)

// Current public SDK: pooled HTTP/2 and the project-scoped ULID document contract.
replace terrascale.tech/sdk => github.com/TerraScale/ts-go v0.0.0-20261028013942-07ffb51e1d66
