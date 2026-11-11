# TerraBase consumers

ts-auth persists its state in TerraBase SQL tables through `TerraBaseSqlClient` from this SDK, consumed as source
(`$(TsDotnetRoot)/src/TerraScale.DotNet`) from the same monorepo revision, not as a package.

## Routes

Change a route in the clients and in `contracts/terrabase/routes.json` together. The monorepo's journey calls the
clients against the TerraBase built from the same commit.

Configure the supplied `HttpClient` with `TerraBaseHttp.CreateHandler()` so a redirect cannot forward request bodies
or credentials to another origin. The SDK does not reconfigure or dispose the supplied client.
