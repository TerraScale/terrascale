# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet 0.0.12 from merged ts-dotnet revision
`75877b6003689aa34cf015d682085450c9b2e95e` (PR65). It adds bounded project user
export pages and optional plain-text branding copy for authentication emails.

Two clean locked builds produced identical package bytes. Installed-package
HTTP fake consumers passed on net10.0 and net11.0. This is a locally built
package; publication is not claimed. Dashboard HTTP journeys exercise the
installed export operation against its controlled ts-auth producer.

`provenance.json` records the source revision, toolchain, package and assembly
hashes and every packed contract hash. CI verifies these bytes and the central
pin. Earlier artifacts are retained; only 0.0.12 is selected.
