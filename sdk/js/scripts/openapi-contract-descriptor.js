import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";

/** @typedef {{ [key: string]: unknown }} JsonObject */

/**
 * @typedef {object} OpenApiSpec
 * @property {Record<string, OpenApiPathItem | undefined>} paths
 * @property {unknown} [security]
 */

/** @typedef {{ parameters?: unknown[]; [key: string]: unknown }} OpenApiPathItem */

/**
 * @typedef {object} OpenApiOperation
 * @property {unknown} [operationId]
 * @property {unknown[]} [parameters]
 * @property {unknown} [requestBody]
 * @property {Record<string, unknown>} [responses]
 * @property {unknown} [security]
 */

/**
 * @typedef {object} ManifestRoute
 * @property {string} base
 * @property {string} client
 * @property {string} [producer]
 * @property {string} operation
 * @property {string} routeName
 * @property {string} method
 * @property {string} pathTemplate
 * @property {string} [requestType]
 * @property {string} [responseType]
 */

/**
 * @typedef {object} RouteDescriptor
 * @property {string} method
 * @property {string} pathTemplate
 * @property {string} routeName
 */

/**
 * @typedef {object} ParameterDescriptor
 * @property {string} in
 * @property {string} name
 * @property {boolean} required
 * @property {unknown} parameter
 * @property {unknown} schema
 */

/**
 * @typedef {object} OpenApiContractDescriptorInput
 * @property {string} manifestPath
 * @property {string} specPath
 * @property {{ edge?: string | undefined } | undefined} [specPaths]
 */

/**
 * @param {OpenApiContractDescriptorInput} input
 * @returns {Promise<{
 *   descriptorVersion: number;
 *   contractVersion?: string;
 *   routeCount: number;
 *   routes: RouteDescriptor[];
 *   sources: {
 *     openApiSha256: string;
 *     edgeOpenApiSha256?: string;
 *     routeManifestSha256: string;
 *   };
 * }>}
 */
export async function generateOpenApiContractDescriptor({
  manifestPath,
  specPath,
  specPaths = {},
}) {
  const [manifestBytes, specBytes, edgeSpecBytes] = await Promise.all([
    readFile(path.resolve(process.cwd(), manifestPath)),
    readFile(path.resolve(process.cwd(), specPath)),
    specPaths.edge === undefined
      ? undefined
      : readFile(path.resolve(process.cwd(), specPaths.edge)),
  ]);
  const manifest = JSON.parse(manifestBytes.toString("utf8"));
  const spec = JSON.parse(specBytes.toString("utf8"));
  /** @type {Record<string, OpenApiSpec | undefined>} */
  const specs = {
    terrabase: spec,
    ...(edgeSpecBytes === undefined
      ? {}
      : { edge: JSON.parse(edgeSpecBytes.toString("utf8")) }),
  };
  if (
    !Array.isArray(manifest.routes) ||
    !spec.paths ||
    typeof spec.paths !== "object"
  ) {
    throw new Error(
      "Route manifest and OpenAPI spec must contain routes and paths.",
    );
  }

  const routes = manifest.routes
    .map((/** @type {ManifestRoute} */ route) => {
      const routeSpec = specs[route.producer ?? "terrabase"] ?? spec;
      const operation = findOperation(routeSpec, route);
      if (operation === undefined) {
        throw new Error(
          `SDK route has no OpenAPI operation: ${route.method} ${route.pathTemplate}.`,
        );
      }
      if (
        typeof operation.operationId !== "string" ||
        operation.operationId.length === 0
      ) {
        throw new Error(
          `OpenAPI operation has no operationId: ${route.method} ${route.pathTemplate}.`,
        );
      }
      // The operation lookup above proves the owning path item exists.
      const pathItem = /** @type {OpenApiPathItem} */ (
        findPathItem(routeSpec, route)
      );
      const request = requestDescriptor(operation, routeSpec);
      const responses = successfulResponses(operation, routeSpec);

      return /** @type {RouteDescriptor} */ (
        canonicalize(
          {
            base: route.base,
            client: route.client,
            method: route.method,
            operation: route.operation,
            operationId: operation.operationId,
            parameters: [
              ...(pathItem.parameters ?? []),
              ...(operation.parameters ?? []),
            ]
              .map((parameter) => parameterDescriptor(parameter, routeSpec))
              .sort(compareParameter),
            pathTemplate: route.pathTemplate,
            request,
            responses,
            routeName: route.routeName,
            sdkRequestType: route.requestType ?? "void",
            sdkResponseType: route.responseType ?? "void",
            security: canonicalize(
              operation.security ?? routeSpec.security ?? [],
              routeSpec,
            ),
            errors: Object.entries(operation.responses ?? {})
              .filter(([status]) => !/^2/u.test(status))
              .map(([status, response]) => ({
                status,
                response: canonicalize(response, routeSpec),
              })),
          },
          routeSpec,
        )
      );
    })
    .sort(
      (
        /** @type {RouteDescriptor} */ left,
        /** @type {RouteDescriptor} */ right,
      ) =>
        `${left.method} ${left.pathTemplate} ${left.routeName}`.localeCompare(
          `${right.method} ${right.pathTemplate} ${right.routeName}`,
        ),
    );

  return {
    descriptorVersion: 2,
    ...(typeof manifest.contractVersion === "string"
      ? { contractVersion: manifest.contractVersion }
      : {}),
    routeCount: routes.length,
    routes,
    sources: {
      openApiSha256: sha256(specBytes),
      ...(edgeSpecBytes === undefined
        ? {}
        : { edgeOpenApiSha256: sha256(edgeSpecBytes) }),
      routeManifestSha256: sha256(manifestBytes),
    },
  };
}

/**
 * @param {OpenApiContractDescriptorInput & { outputPath: string }} input
 * @returns {ReturnType<typeof generateOpenApiContractDescriptor>}
 */
export async function writeOpenApiContractDescriptor({
  manifestPath,
  outputPath,
  specPath,
  specPaths,
}) {
  const descriptor = await generateOpenApiContractDescriptor({
    manifestPath,
    specPath,
    specPaths,
  });
  await writeFile(outputPath, `${JSON.stringify(descriptor, null, 2)}\n`);
  return descriptor;
}

/**
 * @param {OpenApiOperation} operation
 * @param {OpenApiSpec} spec
 * @returns {unknown}
 */
function requestDescriptor(operation, spec) {
  const body =
    operation.requestBody === undefined
      ? undefined
      : /** @type {{ required?: unknown; content?: Record<string, { schema?: unknown }> }} */ (
          resolveSchema(operation.requestBody, spec)
        );
  if (body === undefined) return { kind: "void" };
  return {
    requestBody: canonicalize(operation.requestBody, spec),
    required: body.required === true,
    content: contentSchemas(body.content, spec),
  };
}

/**
 * @param {OpenApiOperation} operation
 * @param {OpenApiSpec} spec
 * @returns {{ status: string; response: unknown }[]}
 */
function successfulResponses(operation, spec) {
  return Object.entries(operation.responses ?? {})
    .filter(([status]) => /^2(?:\d\d|xx)$/iu.test(status))
    .map(([status, response]) => ({
      status,
      response: canonicalize(response, spec),
    }))
    .sort((left, right) => left.status.localeCompare(right.status));
}

/**
 * @param {unknown} parameter
 * @param {OpenApiSpec} spec
 * @returns {ParameterDescriptor}
 */
function parameterDescriptor(parameter, spec) {
  const resolved =
    /** @type {{ in: string; name: string; required?: unknown; schema?: unknown }} */ (
      resolveSchema(parameter, spec)
    );
  return {
    in: resolved.in,
    name: resolved.name,
    required: resolved.required === true,
    parameter: canonicalize(parameter, spec),
    schema: schemaDescriptor(resolved.schema, spec),
  };
}

/**
 * @param {Record<string, { schema?: unknown }> | undefined} content
 * @param {OpenApiSpec} spec
 * @returns {{ mediaType: string; schema: unknown }[]}
 */
function contentSchemas(content, spec) {
  return Object.entries(content ?? {})
    .map(([mediaType, media]) => ({
      mediaType,
      schema: schemaDescriptor(media.schema, spec),
    }))
    .sort((left, right) => left.mediaType.localeCompare(right.mediaType));
}

/**
 * @param {unknown} schema
 * @param {OpenApiSpec} spec
 * @returns {unknown}
 */
function schemaDescriptor(schema, spec) {
  if (schema === undefined) return { kind: "void" };
  return canonicalize(schema, spec);
}

/**
 * @param {OpenApiSpec} spec
 * @param {ManifestRoute} route
 * @returns {OpenApiPathItem | undefined}
 */
function findPathItem(spec, route) {
  return Object.entries(spec.paths).find(
    ([pathTemplate, pathItem]) =>
      comparablePath(pathTemplate) === comparablePath(route.pathTemplate) &&
      pathItem?.[route.method.toLowerCase()] !== undefined,
  )?.[1];
}

/**
 * @param {OpenApiSpec} spec
 * @param {ManifestRoute} route
 * @returns {OpenApiOperation | undefined}
 */
function findOperation(spec, route) {
  return /** @type {OpenApiOperation | undefined} */ (
    findPathItem(spec, route)?.[route.method.toLowerCase()]
  );
}

/**
 * @param {string} pathTemplate
 * @returns {string}
 */
function comparablePath(pathTemplate) {
  return pathTemplate;
}

/**
 * @param {ParameterDescriptor} left
 * @param {ParameterDescriptor} right
 * @returns {number}
 */
function compareParameter(left, right) {
  return `${left.in}:${left.name}`.localeCompare(`${right.in}:${right.name}`);
}

/**
 * @param {unknown} value
 * @param {OpenApiSpec} spec
 * @param {Set<string>} [seen]
 * @returns {unknown}
 */
function canonicalize(value, spec, seen = new Set()) {
  if (Array.isArray(value))
    return value.map((item) => canonicalize(item, spec, seen));
  if (!isJsonObject(value)) return value;
  if (typeof value.$ref === "string") {
    if (seen.has(value.$ref)) return { $ref: value.$ref };
    seen.add(value.$ref);
    const resolved = canonicalize(
      resolveSchema({ $ref: value.$ref }, spec),
      spec,
      seen,
    );
    seen.delete(value.$ref);
    const siblings = Object.fromEntries(
      Object.entries(value)
        .filter(([key]) => key !== "$ref")
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([key, entry]) => [key, canonicalize(entry, spec, seen)]),
    );
    return Object.keys(siblings).length === 0
      ? { $ref: value.$ref, resolved }
      : { $ref: value.$ref, siblings, resolved };
  }
  return Object.fromEntries(
    Object.entries(value)
      .filter(([, entry]) => entry !== undefined)
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([key, entry]) => [key, canonicalize(entry, spec, seen)]),
  );
}

/**
 * @param {unknown} value
 * @param {OpenApiSpec} spec
 * @returns {unknown}
 */
function resolveSchema(value, spec) {
  if (!isJsonObject(value) || typeof value.$ref !== "string") return value;
  const reference = value.$ref;
  if (!reference.startsWith("#/")) return value;
  const resolved = reference
    .slice(2)
    .split("/")
    .reduce(
      (/** @type {unknown} */ current, segment) =>
        isJsonObject(current)
          ? current[segment.replaceAll("~1", "/").replaceAll("~0", "~")]
          : undefined,
      spec,
    );
  if (resolved === undefined)
    throw new Error(`OpenAPI reference cannot be resolved: ${reference}.`);
  return resolved;
}

/**
 * @param {unknown} value
 * @returns {value is JsonObject}
 */
function isJsonObject(value) {
  return Boolean(value) && typeof value === "object";
}

/**
 * @param {Buffer} bytes
 * @returns {string}
 */
function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}
