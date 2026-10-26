# ts-dotnet package pin

This held review branch consumes the local TerraScale.DotNet0.0.13 package from
`eda3de6b2b484bdd3b322e071bf3a56702ab0f0d` (PR73). It adds rename and the nullable
`previousName`/`conflictOf` metadata from merged TerraBase689.

The package was built twice from clean locked source with identical bytes;
installed-package HTTP fixture consumers passed on net10.0/net11.0. This is local
reproducibility and transport evidence, not publication or live qualification.
Hold merge and automatic deployment for the owning rename API, safe ULID scope
staging, required CI, SDK release and the orchestrator dashboard release window.
Names are unique per project after convergence; concurrent cross-region claims
resolve to one winner. No exclusive cross-region admission is promised.

`provenance.json` records source, package/assembly hashes and every packed contract.
CI verifies these bytes and the central pin. The previousadff15f5 artifact remains
untouched in the task artifact directory; the selected review package changed
deliberately to carry conflict metadata. No package was published.
