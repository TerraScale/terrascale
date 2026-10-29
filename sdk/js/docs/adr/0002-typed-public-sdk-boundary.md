# Typed public SDK boundary

Status: Accepted source boundary; production qualification remains required.

Expose named native document operations, exact typed values and pure schema/SQL
builders through declared package exports. The current native route manifest
contains five scoped `/v1` operations and derives only from exact owning producer
OpenAPI bytes. Security, requests, success responses, and error responses are
part of descriptor parity. Hosted identity has a separate named client and
private transport. No generic transport is publicly exported.

Catalog provisioning, resource lifecycle and credentials require C06/C07
admission. Missing capabilities raise explicit errors rather than introducing
unpublished remote DTOs or routes. Installed-package tests enforce this boundary;
a successful build alone does not prove real producer compatibility.
