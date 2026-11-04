# Typed public SDK boundary

Status: Accepted source boundary; production qualification remains required.

Expose named SQL and catalog operations and exact typed values through declared
package exports. The route manifest derives only from the pinned owning producer
handler sources; methods, paths, media types, scopes and success statuses are part
of its parity. Hosted identity has a separate named client and
private transport. No generic transport is publicly exported.

Catalog provisioning, resource lifecycle and credentials require C06/C07
admission. Missing capabilities raise explicit errors rather than introducing
unpublished remote DTOs or routes. Installed-package tests enforce this boundary;
a successful build alone does not prove real producer compatibility.
