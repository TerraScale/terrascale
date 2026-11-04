import { v1Routes } from "../src/v1/routes.js";
import { currentSourcePin, verifyCurrentSourceRoutes } from "./terrabase-current-source.mjs";

/** The database catalog route manifest, verified against the pinned producer handler sources. */
export async function generateV1Manifest() {
  return {
    manifestVersion: 5,
    contractVersion: "terrabase-csharp-v1",
    currentSource: currentSourcePin,
    currentRouteEvidence: verifyCurrentSourceRoutes(v1Routes),
    currentGeneratedOpenApi: "Unavailable: current route parity is source-bound, not runtime qualification.",
    routeCount: v1Routes.length,
    routes: v1Routes.map(route => ({
      producer: "terrabase-csharp", client: "TerraBaseV1Client",
      operation: route.operation, method: route.method, pathTemplate: route.pathTemplate,
      permission: route.permission, requestMediaTypes: route.requestMediaTypes,
      responseMediaType: route.responseMediaType, successStatuses: route.successStatuses,
      idempotencyKey: route.idempotencyKey, replaySafe: route.replaySafe, evidence: "current-handler-source",
    })),
  };
}
