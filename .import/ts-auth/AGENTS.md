AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# ts-auth

Hosted identity provider (Clerk/Auth0/WorkOS class) for the TerraScale
platform, built on TerraBase through the TerraScale.DotNet SDK.

## Rules

- The project must be C# (.NET 10), feature sliced: one folder under
  `src/TerraScale.Auth/` per feature, endpoints registered in that feature's
  module file (see `EndpointModules`).
- We use TUnit for tests, run with
  `dotnet run --project tests/TerraScale.Auth.Tests/TerraScale.Auth.Tests.csproj -c Release`
  (never `dotnet test`).
- E2E tests boot the real service over the real embedded TerraBase engine via
  the real SDK (`AuthServiceFixture`). No storage fakes.
- Build requires sibling checkouts of `ts-dotnet` and `terrabase` next to this
  repo (CI clones them; see `.forgejo/workflows/ci.yml`).
- Deploy via the dotnet container way: single-file self-contained publish into
  the distroless `Containerfile`, image pushed SHA-tagged by CI.
- Security-sensitive code (Crypto/, WebAuthn/) is written from scratch on
  purpose — do not replace it with third-party packages without a human
  decision recorded here.
- No Newtonsoft.Json (System.Text.Json only). No new NuGet packages without a
  strong reason; the point of this service is to demonstrate the platform.
