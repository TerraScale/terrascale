# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet 0.0.11 from merged ts-dotnet revision
`03881aa74a52683112180a5035d7b5ced8d65032` (PR63). It adds service-authenticated deletion
outbox list and exact-proof acknowledgement APIs and project closure evidence.

Two clean locked builds produced identical package bytes. Installed-package
HTTP fake consumers, including the deletion outbox APIs, passed on net10.0 and
net11.0. This is a locally built package; publication and live-service parity
are not claimed.

`provenance.json` records the source revision, toolchain, package and assembly
hashes and every packed contract hash. CI verifies these bytes and the central
pin. Earlier 0.0.8 and 0.0.9 artifacts are retained; only 0.0.11 is selected.
