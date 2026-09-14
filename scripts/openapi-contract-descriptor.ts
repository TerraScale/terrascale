import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";

export async function generateOpenApiContractDescriptor({
  manifestPath,
  specPath,
  excludedContracts = [],
  includeFullOperation = false,
}) {
  const [manifestBytes, specBytes] = await Promise.all([
    readFile(path.resolve(process.cwd(), manifestPath)),
    readFile(path.resolve(process.cwd(), specPath)),
    specPaths.edge === undefined ? undefined : readFile(path.resolve(process.cwd(), specPaths.edge)),
  ]);
  const manifest = JSON.parse(manifestBytes.toString("utf8"));
  const spec = JSON.parse(specBytes.toString("utf8"));
  const specs = { terrabase: spec, ...(edgeSpecBytes === undefined ? {} : { edge: JSON.parse(edgeSpecBytes.toString("utf8")) }) };
  if (!Array.isArray(manifest.routes) || !spec.paths || typeof spec.paths !== "object") {
    throw new Error("Route manifest and OpenAPI spec must contain routes and paths.");
  }

  const excluded = new Set(excludedContracts);
  const routes = manifest.routes.filter((route) => !excluded.has(route.contract)).map((route) => {
    const operation = findOperation(spec, route);
    if (operation === undefined) {
      throw new Error(`SDK route has no OpenAPI operation: ${route.method} ${route.pathTemplate}.`);
    }
    if (typeof operation.operationId !== "string" || operation.operationId.length === 0) {
      throw new Error(`OpenAPI operation has no operationId: ${route.method} ${route.pathTemplate}.`);
    }
    const pathItem = findPathItem(routeSpec, route);
    const request = requestDescriptor(operation, routeSpec);
    const responses = successfulResponses(operation, routeSpec);
    assertSdkContract(route, request, responses);
    return canonicalize({
      base: route.base,
      client: route.client,
      method: route.method,
      operation: route.operation,
      operationId: operation.operationId,
      openApiContract: includeFullOperation
        ? completeOperationDescriptor(operation, spec)
        : undefined,
      parameters: [...(pathItem.parameters ?? []), ...(operation.parameters ?? [])]
        .map((parameter) => parameterDescriptor(parameter, routeSpec))
        .sort(compareParameter),
      pathTemplate: route.pathTemplate,
      request,
      responses,
      routeName: route.routeName,
      sdkRequestType: route.requestType ?? "void",
      sdkResponseType: route.responseType ?? "void",
      sourceContract: route.sourceContract,
    }, routeSpec);
  }).sort((left, right) => `${left.method} ${left.pathTemplate} ${left.routeName}`.localeCompare(`${right.method} ${right.pathTemplate} ${right.routeName}`));

  return canonicalize({
    contractProfile: includeFullOperation ? "complete-operation" : undefined,
    descriptorVersion: 2,
    excludedContracts: excludedContracts.length === 0
      ? undefined
      : [...new Set(excludedContracts)].sort(),
    routeCount: routes.length,
    routes,
    sources: {
      openApiSha256: sha256(specBytes),
      ...(edgeSpecBytes === undefined ? {} : { edgeOpenApiSha256: sha256(edgeSpecBytes) }),
      routeManifestSha256: sha256(manifestBytes),
    },
  };
}

export function fingerprintOpenApiContractDescriptor(descriptor) {
  if (
    !descriptor ||
    descriptor.contractProfile !== "complete-operation" ||
    !Array.isArray(descriptor.routes) ||
    descriptor.routes.some((route) => !route?.openApiContract) ||
    !descriptor.sources
  ) {
    throw new Error(
      "A complete-operation OpenAPI contract descriptor is required for fingerprinting.",
    );
  }
  return {
    descriptorVersion: 2,
    routeCount: descriptor.routeCount,
    routes: descriptor.routes.map((route) => ({
      method: route.method,
      pathTemplate: route.pathTemplate,
      routeName: route.routeName,
      operationId: route.operationId,
      contractSha256: sha256(JSON.stringify(route)),
    })),
    sources: descriptor.sources,
  };
}

function assertSdkContract(route, request, responses) {
  // Routes generated directly from terrabase OpenAPI intentionally have no
  // captured request. Source-contract assertions only apply to legacy capture
  // metadata when it is present.
  if (route.sourceContract === undefined) return;
  const requestType = route.requestType ?? "void";
  const sourceBody = route.sourceContract?.body?.kind ?? "void";
  if (requestType !== "void" && requestType !== "never" && sourceBody === "void") {
    throw new Error(`SDK request metadata drift: ${route.method} ${route.pathTemplate} declares ${requestType} but captures no request body.`);
  }
  if ((requestType === "void" || requestType === "never") && sourceBody !== "void") {
    throw new Error(`SDK request metadata drift: ${route.method} ${route.pathTemplate} declares ${requestType} but captures a request body.`);
  }
  const responseType = route.responseType ?? "void";
  if (responseType === "never" && responses.length !== 0) {
    throw new Error(`SDK/OpenAPI response never mismatch: ${route.method} ${route.pathTemplate} publishes successful responses.`);
  }
}

function completeOperationDescriptor(operation, spec) {
  const requirements = operation.security ?? spec.security ?? [];
  if (!Array.isArray(requirements)) {
    throw new Error("OpenAPI security requirements must be an array.");
  }
  const declaredSchemes = spec.components?.securitySchemes ?? {};
  const schemeNames = [...new Set(
    requirements.flatMap((requirement) => Object.keys(requirement ?? {})),
  )].sort();
  const schemes = Object.fromEntries(schemeNames.map((name) => {
    if (!Object.hasOwn(declaredSchemes, name)) {
      throw new Error(`OpenAPI security scheme cannot be resolved: ${name}.`);
    }
    return [name, declaredSchemes[name]];
  }));
  return { operation, security: { requirements, schemes } };
}

export async function writeOpenApiContractDescriptor({
  manifestPath,
  outputPath,
  specPath,
  excludedContracts = [],
}) {
  const descriptor = await generateOpenApiContractDescriptor({
    manifestPath,
    specPath,
    excludedContracts,
  });
  await writeFile(outputPath, `${JSON.stringify(descriptor, null, 2)}\n`);
  return descriptor;
}

function requestDescriptor(operation, spec) {
  const body = operation.requestBody === undefined ? undefined : resolveSchema(operation.requestBody, spec);
  if (body === undefined) return { kind: "void" };
  return { requestBody: canonicalize(operation.requestBody, spec), required: body.required === true, content: contentSchemas(body.content, spec) };
}

function successfulResponses(operation, spec) {
  return Object.entries(operation.responses ?? {})
    .filter(([status]) => /^2(?:\d\d|xx)$/iu.test(status))
    .map(([status, response]) => ({ status, response: canonicalize(response, spec) }))
    .sort((left, right) => left.status.localeCompare(right.status));
}

function parameterDescriptor(parameter, spec) {
  const resolved = resolveSchema(parameter, spec);
  return { in: resolved.in, name: resolved.name, required: resolved.required === true, parameter: canonicalize(parameter, spec), schema: schemaDescriptor(resolved.schema, spec) };
}

function contentSchemas(content, spec) {
  return Object.entries(content ?? {}).map(([mediaType, media]) => ({ mediaType, schema: schemaDescriptor(media.schema, spec) }))
    .sort((left, right) => left.mediaType.localeCompare(right.mediaType));
}

function schemaDescriptor(schema, spec) {
  if (schema === undefined) return { kind: "void" };
  return canonicalize(schema, spec);
}

function findPathItem(spec, route) {
  return Object.entries(spec.paths).find(([pathTemplate, pathItem]) => comparablePath(pathTemplate) === comparablePath(route.pathTemplate) && pathItem?.[route.method.toLowerCase()] !== undefined)?.[1];
}

function findOperation(spec, route) {
  return findPathItem(spec, route)?.[route.method.toLowerCase()];
}

function comparablePath(pathTemplate) {
  return pathTemplate.replace(/\{[^}]+\}/gu, "{parameter}").replace(/\/+$/u, "");
}

function compareParameter(left, right) {
  return `${left.in}:${left.name}`.localeCompare(`${right.in}:${right.name}`);
}

function canonicalize(value, spec, seen = new Set()) {
  if (Array.isArray(value)) return value.map((item) => canonicalize(item, spec, seen));
  if (!value || typeof value !== "object") return value;
  if (typeof value.$ref === "string") {
    if (seen.has(value.$ref)) return { $ref: value.$ref };
    seen.add(value.$ref);
    const resolved = canonicalize(resolveSchema({ $ref: value.$ref }, spec), spec, seen);
    seen.delete(value.$ref);
    const siblings = Object.fromEntries(Object.entries(value).filter(([key]) => key !== "$ref").sort(([left], [right]) => left.localeCompare(right)).map(([key, entry]) => [key, canonicalize(entry, spec, seen)]));
    return Object.keys(siblings).length === 0 ? { $ref: value.$ref, resolved } : { $ref: value.$ref, siblings, resolved };
  }
  return Object.fromEntries(Object.entries(value).filter(([, entry]) => entry !== undefined).sort(([left], [right]) => left.localeCompare(right)).map(([key, entry]) => [key, canonicalize(entry, spec, seen)]));
}

function resolveSchema(value, spec) {
  if (!value || typeof value !== "object" || typeof value.$ref !== "string") return value;
  const reference = value.$ref;
  if (!reference.startsWith("#/")) return value;
  const resolved = reference.slice(2).split("/").reduce((current, segment) => current?.[segment.replaceAll("~1", "/").replaceAll("~0", "~")], spec);
  if (resolved === undefined) throw new Error(`OpenAPI reference cannot be resolved: ${reference}.`);
  return resolved;
}

function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}
