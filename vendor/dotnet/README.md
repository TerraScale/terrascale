# ts-dotnet package pin

This review branch consumes a local TerraScale.DotNet 0.0.13 package from
`adff15f5d44ac1db5fbb6ffcb20555a8aeb1b43f` (PR73). It adds database rename
and immutable-ID selectors to the current SDK contract.

The producer reports local transport fixtures and a package build only.
This artifact is unqualified for CI, reproducibility, publication and live use.
Hold merge and automatic dashboard deployment for the rename API, exclusive
namespace decisions, ULID scopes and a qualified published SDK.

`provenance.json` records the source revision, package and assembly
hashes and every packed contract hash. CI verifies these bytes and the central
pin. Earlier artifacts are retained; only this local 0.0.13 is selected.
