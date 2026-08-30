import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import path from "node:path";

const sha256Pattern = /^[0-9a-f]{64}$/u;

export async function loadNpmContractEvidence(repositoryRoot, packedRouteManifestPath) {
  const routeManifestPath = path.join(repositoryRoot, "sdk-route-manifest.json");
  const descriptorPath = path.join(repositoryRoot, "sdk-openapi-contract-descriptor.json");
  const [routeManifestBytes, descriptorBytes] = await Promise.all([
    readFile(routeManifestPath),
    readFile(descriptorPath),
  ]);
  const routeManifest = parseObject(routeManifestBytes, "SDK route manifest");
  const descriptor = parseObject(descriptorBytes, "SDK OpenAPI descriptor");
  const routeManifestSha256 = sha256(routeManifestBytes);

  if (descriptor.descriptorVersion !== 2) {
    throw new Error("SDK OpenAPI descriptor version 2 is required for npm qualification.");
  }
  if (!Number.isSafeInteger(descriptor.routeCount) || descriptor.routeCount < 1) {
    throw new Error("SDK OpenAPI descriptor routeCount must be a positive safe integer.");
  }
  if (descriptor.routeCount !== routeManifest.routeCount) {
    throw new Error("SDK OpenAPI descriptor and route manifest routeCount values disagree.");
  }
  if (!Array.isArray(descriptor.routes) || descriptor.routes.length !== descriptor.routeCount) {
    throw new Error("SDK OpenAPI descriptor routes must match its declared routeCount.");
  }
  if (!Array.isArray(routeManifest.routes) || routeManifest.routes.length !== routeManifest.routeCount) {
    throw new Error("SDK route manifest routes must match its declared routeCount.");
  }

  const sources = requireObject(descriptor.sources, "SDK OpenAPI descriptor sources");
  const openApiSha256 = requireSha256(sources.openApiSha256, "Dashboard OpenAPI");
  const declaredRouteManifestSha256 = requireSha256(
    sources.routeManifestSha256,
    "SDK route manifest",
  );
  if (declaredRouteManifestSha256 !== routeManifestSha256) {
    throw new Error("SDK OpenAPI descriptor does not bind the checked-in route manifest bytes.");
  }

  if (packedRouteManifestPath !== undefined) {
    const packedRouteManifestSha256 = sha256(await readFile(packedRouteManifestPath));
    if (packedRouteManifestSha256 !== routeManifestSha256) {
      throw new Error("Packed SDK route manifest differs from the qualified source bytes.");
    }
  }

  return {
    descriptorVersion: descriptor.descriptorVersion,
    descriptorSha256: sha256(descriptorBytes),
    openApiSha256,
    routeManifestSha256,
    routeCount: descriptor.routeCount,
  };
}

function parseObject(bytes, name) {
  let value;
  try {
    value = JSON.parse(bytes.toString("utf8"));
  } catch {
    throw new Error(`${name} must contain valid JSON.`);
  }
  return requireObject(value, name);
}

function requireObject(value, name) {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error(`${name} must be an object.`);
  }
  return value;
}

function requireSha256(value, name) {
  if (typeof value !== "string" || !sha256Pattern.test(value)) {
    throw new Error(`${name} digest must be 64 lowercase hexadecimal characters.`);
  }
  return value;
}

function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}
