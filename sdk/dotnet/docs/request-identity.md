# Request identity on SDK errors

`TerraBaseException.RequestId` and `TerraScaleAuthException.RequestId` expose
`X-Request-ID` after the response has been disposed. This includes HTTP failures
and malformed successful responses rejected while the transport reads them.
The value is diagnostic correlation, never authorization or tenant identity.

Only one canonical uppercase 26-character ULID is accepted. Missing, repeated,
combined, oversized or invalid values produce `null`. The SDK does not generate
an identity, read one from the error body, or propagate one in outgoing headers.
Errors without an HTTP response have no responder identity.

This is an additive API change with no storage, authentication or retry change.
The existing package version remains 0.0.11. Publish these APIs in a new package
version through the existing matching `v0.0.X` tag release workflow; merging the
PR alone does not publish a package. No third-party dependency upgrade is needed.

Until the new package is published and installed, dashboard consumers of 0.0.11
can extract the TerraBase identity from the existing response-header snapshot:

```csharp
string? requestId = error.ResponseHeaders.TryGetValue("X-Request-ID", out var values)
    && values.Count == 1 && TerraScaleIds.IsValid(values[0]) ? values[0] : null;
```

Use `TerraScale.DotNet.TerraBase.TerraBaseException` for `error` and import
`TerraScale.DotNet` for the validator. The 0.0.11 auth exception does not retain
response headers; a caller-owned HTTP handler can observe them until the new
package is installed. Validate that observation with the same single-ULID rule.
