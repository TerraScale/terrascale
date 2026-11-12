# Public serving-region catalog

Producer: `TerraScale/terrascale` revision `a3cbf8b1c73d304189a489fa8033d41f3b508de3`. Pins were computed from `git show <revision>:<path>`, not the producer working tree.

| Pinned source | SHA-256 |
|---|---|
| `src/TerraBase/Features/Regions/ListRegionsEndpoint.cs` | `bcf001ba16b29cf334dd72a912e71034e72af56bf5507180694ef5fdf33e0762` |
| `src/TerraBase/Features/Regions/RegionCatalog.cs` | `914b3f36cc61760ae89ce12e4d210219424536c8803eef6e6fe812d9f91155d8` |
| `src/TerraBase/Configuration/TerraBaseOptions.cs` | `4e9d248d71b2cc1c513357f8cd24916778f5abf49e236fd294db9cf1b011aed3` |
| `src/TerraBase/Hosting/TerraBaseJsonContext.cs` | `448816c44361d03fbe5bce6168e9f743d4847f903ca489ed44131617e875c026` |
| `docs/terrabase/region-catalog.md` | `0bb9d6a3fb3ef34609dddf9503257694f5e0cc65f2a7fe0366da11c3cd4779fc` |

Operation `listRegions`: anonymous `GET /v1/regions` on the client listener; no credentials, request body, parameters or pagination. Success is HTTP 200, `application/json`, exactly `{"regions":["fra","gru","sjc"]}` in the producer documentation example. Values come from live membership, include this node's region, are distinct and ordered ordinally, and are not a fixed list. `Cache-Control: public, max-age=5`; the SDK performs a request on each call and adds no cache or poller. The owning endpoint declares no operation-specific error statuses or retry policy. Other HTTP failures retain existing transport errors; this operation is sent once. This is placement metadata and does not change request origins or regional failover.

`TerraBaseOptions.IsRegion` (113–114) requires 1–63 ASCII characters, first `[a-z]`, remaining `[a-z0-9-]`, final character not `-`. Syntax alone does not mean a region serves now. Use catalog ids for placement. `RegionCatalog.cs` 23–30 defines membership/filter/distinct/order; `ListRegionsEndpoint.cs` 15–29 pins method, path, operation identity, anonymous security and cache header; `TerraBaseJsonContext.cs` registers `RegionsBody`. These are source-derived supplemental contracts, not newly generated OpenAPI/route artifacts or live qualification.

Results require a non-null, nonempty `regions` string list with valid sorted distinct ids. Malformed responses fail without inventing fallback regions; future valid ids are preserved.

```csharp
using var handler = TerraBaseHttp.CreateHandler();
using var http = new HttpClient(handler);
var catalog = await new TerraBaseRegionsClient(http).ListRegionsAsync(cancellationToken);
// catalog.Regions supplies placement choices; the client does not own http.
```

Types are in `TerraScale.DotNet.TerraBase`; JSON metadata is source generated with required-constructor and nullable checks. Calls preserve cancellation, deadlines, response bounds and disposal through the existing connection.

The constructor accepts only a caller-owned `HttpClient` and always targets `TerraBaseRegion.PublicOrigin`. Default Authorization or Cookie headers cause an actionable exception before dispatch; use a separate credential-free client and the cookie/redirect-disabled handler above. The SDK does not clear or mutate caller defaults. Shared transport origin validation remains unchanged.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
