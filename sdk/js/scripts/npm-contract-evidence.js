import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import path from "node:path";

const sha256Pattern = /^[0-9a-f]{64}$/u;

/**
 * @typedef {object} NpmContractEvidence
 * @property {number} descriptorVersion
 * @property {string} descriptorSha256
 * @property {string} openApiSha256
 * @property {string} routeManifestSha256
 * @property {number} routeCount
 */

/**
 * @param {string} repositoryRoot
 * @param {string} [packedRouteManifestPath]
 * @returns {Promise<NpmContractEvidence>}
 */
export async function loadNpmContractEvidence(
  repositoryRoot,
  packedRouteManifestPath,
) {
  const routeManifestPath = path.join(
    repositoryRoot,
    "sdk-route-manifest.json",
  );
  const descriptorPath = path.join(
    repositoryRoot,
    "sdk-openapi-contract-descriptor.json",
  );
  const [routeManifestBytes, descriptorBytes, currentPinBytes] =
    await Promise.all([
      readFile(routeManifestPath),
      readFile(descriptorPath),
      readFile(path.join(repositoryRoot, "sdk-current-contract.json")),
    ]);
  const routeManifest = parseObject(routeManifestBytes, "SDK route manifest");
  const descriptor = parseObject(descriptorBytes, "SDK OpenAPI descriptor");
  const currentPin = parseObject(
    currentPinBytes,
    "Current native producer pin",
  );
  const routeManifestSha256 = sha256(routeManifestBytes);

  if (descriptor.descriptorVersion !== 2) {
    throw new Error(
      "SDK OpenAPI descriptor version 2 is required for npm qualification.",
    );
  }
  if (
    !Number.isSafeInteger(descriptor.routeCount) ||
    descriptor.routeCount < 1
  ) {
    throw new Error(
      "SDK OpenAPI descriptor routeCount must be a positive safe integer.",
    );
  }
  if (
    !Array.isArray(routeManifest.routes) ||
    routeManifest.routes.length !== routeManifest.routeCount
  ) {
    throw new Error(
      "SDK route manifest routes must match its declared routeCount.",
    );
  }
  if (descriptor.routeCount !== routeManifest.routeCount) {
    throw new Error(
      "SDK OpenAPI descriptor and route manifest routeCount values disagree.",
    );
  }
  if (descriptor.excludedContracts !== undefined)
    throw new Error(
      "Current native contract cannot exclude producer operations.",
    );
  if (
    !Array.isArray(descriptor.routes) ||
    descriptor.routes.length !== descriptor.routeCount
  ) {
    throw new Error(
      "SDK OpenAPI descriptor routes must match its declared routeCount.",
    );
  }

  const sources = requireObject(
    descriptor.sources,
    "SDK OpenAPI descriptor sources",
  );
  const openApiSha256 = requireSha256(
    sources.openApiSha256,
    "Native producer OpenAPI",
  );
  const declaredRouteManifestSha256 = requireSha256(
    sources.routeManifestSha256,
    "SDK route manifest",
  );
  if (declaredRouteManifestSha256 !== routeManifestSha256) {
    throw new Error(
      "SDK OpenAPI descriptor does not bind the checked-in route manifest bytes.",
    );
  }

  if (
    currentPin.version !== 1 ||
    currentPin.producer !== "terrabase" ||
    requireSha256(
      currentPin.sourceOpenApiSha256,
      "Current producer OpenAPI",
    ) !== openApiSha256
  ) {
    throw new Error(
      "SDK OpenAPI descriptor does not bind the current native producer pin.",
    );
  }
  requireSha256(
    currentPin.sourceAtomicVectorsSha256,
    "Current producer atomic vectors",
  );
  if (
    !Array.isArray(currentPin.routes) ||
    JSON.stringify(
      currentPin.routes.map(({ method, path }) => ({ method, path })),
    ) !==
      JSON.stringify(
        routeManifest.routes.map(({ method, pathTemplate }) => ({
          method,
          path: pathTemplate,
        })),
      )
  ) {
    throw new Error(
      "SDK route manifest differs from the exact current native route set.",
    );
  }

  if (packedRouteManifestPath !== undefined) {
    const packedRouteManifestSha256 = sha256(
      await readFile(packedRouteManifestPath),
    );
    if (packedRouteManifestSha256 !== routeManifestSha256) {
      throw new Error(
        "Packed SDK route manifest differs from the qualified source bytes.",
      );
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

/**
 * @param {Buffer} bytes
 * @param {string} name
 * @returns {Record<string, any>}
 */
function parseObject(bytes, name) {
  let value;
  try {
    value = JSON.parse(bytes.toString("utf8"));
  } catch {
    throw new Error(`${name} must contain valid JSON.`);
  }
  return requireObject(value, name);
}

/**
 * @param {unknown} value
 * @param {string} name
 * @returns {Record<string, any>}
 */
function requireObject(value, name) {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error(`${name} must be an object.`);
  }
  return /** @type {Record<string, any>} */ (value);
}

/**
 * @param {unknown} value
 * @param {string} name
 * @returns {string}
 */
function requireSha256(value, name) {
  if (typeof value !== "string" || !sha256Pattern.test(value)) {
    throw new Error(
      `${name} digest must be 64 lowercase hexadecimal characters.`,
    );
  }
  return value;
}

/**
 * @param {Uint8Array} bytes
 * @returns {string}
 */
function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}
