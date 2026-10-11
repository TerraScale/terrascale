module terrascale.tech/ts-benchmark

go 1.25.0

require (
	github.com/HdrHistogram/hdrhistogram-go v1.1.2
	github.com/oklog/ulid/v2 v2.1.2
	github.com/oracle/oci-go-sdk/v65 v65.126.0
	terrascale.tech/sdk v0.0.0-20261011145515-9f704645aa3a
)

require (
	github.com/gofrs/flock v0.10.0 // indirect
	github.com/sony/gobreaker/v2 v2.4.0 // indirect
	github.com/stretchr/testify v1.11.1 // indirect
	github.com/youmark/pkcs8 v0.0.0-20240726163527-a2c0da244d78 // indirect
	golang.org/x/crypto v0.52.0 // indirect
	golang.org/x/sys v0.45.0 // indirect
)

// TS-832: ts-go main with Database.Delete (#43).
replace terrascale.tech/sdk => git.terrascale.tech/TerraScale/ts-go v0.0.0-20261011145515-9f704645aa3a
