import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";

export async function generateOpenApiContractDescriptor({ manifestPath, specPath }) {
  const [manifest, spec] = await Promise.all([readJson(manifestPath), readJson(specPath)]);
  if (!Array.isArray(manifest.routes) || !spec.paths || typeof spec.paths !== "object") {
    throw new Error("Route manifest and OpenAPI spec must contain routes and paths.");
  }

  const routes = manifest.routes.map((route) => {
    const operation = findOperation(spec, route);
    if (operation === undefined) {
      throw new Error(`SDK route has no OpenAPI operation: ${route.method} ${route.pathTemplate}.`);
    }
    if (typeof operation.operationId !== "string" || operation.operationId.length === 0) {
      throw new Error(`OpenAPI operation has no operationId: ${route.method} ${route.pathTemplate}.`);
    }
    const pathItem = findPathItem(spec, route);
    return canonicalize({
      base: route.base,
      client: route.client,
      method: route.method,
      operation: route.operation,
      operationId: operation.operationId,
      parameters: [...(pathItem.parameters ?? []), ...(operation.parameters ?? [])]
        .map((parameter) => resolveSchema(parameter, spec))
        .map((parameter) => ({ in: parameter.in, name: parameter.name, required: parameter.required === true, schema: parameter.schema }))
        .sort(compareParameter),
      pathTemplate: route.pathTemplate,
      request: requestDescriptor(operation, spec),
      responses: successfulResponses(operation, spec),
      routeName: route.routeName,
      sdkRequestType: route.requestType ?? "void",
      sdkResponseType: route.responseType ?? "void",
    }, spec);
  }).sort((left, right) => `${left.method} ${left.pathTemplate} ${left.routeName}`.localeCompare(`${right.method} ${right.pathTemplate} ${right.routeName}`));

  return canonicalize({ descriptorVersion: 1, routeCount: routes.length, routes }, spec);
}

export async function writeOpenApiContractDescriptor({ manifestPath, outputPath, specPath }) {
  const descriptor = await generateOpenApiContractDescriptor({ manifestPath, specPath });
  await writeFile(outputPath, `${JSON.stringify(descriptor, null, 2)}\n`);
  return descriptor;
}

function requestDescriptor(operation, spec) {
  const body = operation.requestBody === undefined ? undefined : resolveSchema(operation.requestBody, spec);
  if (body === undefined) return { kind: "void" };
  return { required: body.required === true, content: contentSchemas(body.content, spec) };
}

function successfulResponses(operation, spec) {
  return Object.entries(operation.responses ?? {})
    .filter(([status]) => /^2\d\d$/u.test(status))
    .map(([status, response]) => [status, resolveSchema(response, spec)])
    .map(([status, response]) => ({ status, content: contentSchemas(response.content, spec) }))
    .sort((left, right) => left.status.localeCompare(right.status));
}

function contentSchemas(content, spec) {
  return Object.entries(content ?? {}).map(([mediaType, media]) => ({ mediaType, schema: media.schema === undefined ? { kind: "void" } : resolveSchema(media.schema, spec) }))
    .sort((left, right) => left.mediaType.localeCompare(right.mediaType));
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
    const resolved = canonicalize(resolveSchema(value, spec), spec, seen);
    seen.delete(value.$ref);
    return resolved;
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

async function readJson(filePath) {
  return JSON.parse(await readFile(path.resolve(process.cwd(), filePath), "utf8"));
}
