// Contract pin for the Go SDK code generation source.
//
// Reachability at scaffold time:
//   - ../terrabase/openapi.json: NOT PRESENT (no terrabase checkout adjacent
//     to ts-go exposes a published openapi.json yet).
//   - ../ts-typescript/sdk-route-manifest.json: PRESENT (routeCount 60).
//
// Therefore: management routes in management.go mirror the TypeScript
// src/client.ts path templates from that manifest; document, schema, SQL,
// reactive, and tenant paths are a minimal hand-written stub against
// /api/v1 per GOAL D-13/D-54/D-14/D-51. Run `make contracts` (see README.md)
// once terrabase publishes versioned contract artifacts, then regenerate.
package sdk

// ContractPin describes the contract source this SDK revision was built from.
const ContractPin = "ts-typescript/sdk-route-manifest.json routeCount=60 + hand-written /api/v1 stub (terrabase openapi.json unreachable at scaffold)"

// ContractManifestRouteCount is the route count observed in the TypeScript
// route manifest at scaffold time. Management path parity is checked against it.
const ContractManifestRouteCount = 60
