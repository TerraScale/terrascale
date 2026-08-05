#!/usr/bin/env node

import { execFile } from "node:child_process";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { promisify } from "node:util";

const httpMethods = new Set(["delete", "get", "head", "options", "patch", "post", "put", "trace"]);
const uncoveredDisplayLimit = 25;
const defaultBaseline = "typed-contract-debt-baseline.json";
const execFileAsync = promisify(execFile);

if (isMainModule()) {
  process.exitCode = await runOpenApiDriftCli(process.argv.slice(2));
}

export async function runOpenApiDriftCli(args, output = console) {
  const strict = args.includes("--strict");
  const [specReference, manifestReference = "sdk-route-manifest.json"] = args.filter(
    (argument) => !argument.startsWith("--"),
  );

  if (specReference === undefined) {
    output.error(
      "Usage: node scripts/check-openapi-drift.mjs <openapi-spec-path-or-url> [manifest-path]",
    );
    return 2;
  }

  try {
    const result = await checkOpenApiDrift(specReference, manifestReference, strict ? undefined : defaultBaseline, strict);
    const formatted = formatOpenApiDriftResult(result, specReference, manifestReference);
    writeFormattedResult(formatted, output);
    return exitCodeForOpenApiDriftResult(result);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    output.error(`OpenAPI drift check could not run: ${message}`);
    return 2;
  }
}

export async function checkOpenApiDrift(
  specReference,
  manifestReference = "sdk-route-manifest.json",
  baselineReference = defaultBaseline,
  strict = false,
) {
  const [spec, manifest, specSnapshot, baseline] = await Promise.all([
    readJson(specReference),
    readJson(manifestReference),
    readSpecSnapshotInfo(specReference),
    baselineReference === undefined ? Promise.resolve([]) : Array.isArray(baselineReference) ? Promise.resolve(baselineReference) : readJson(baselineReference),
  ]);
  validateTypedContractBaseline(baseline);

  return {
    ...compareOpenApiDrift(manifest, spec, baseline),
    specSnapshot,
    strict,
  };
}

export function validateTypedContractBaseline(baseline) {
  if (!Array.isArray(baseline)) throw new Error("Typed-contract debt baseline must be an array.");
  const keys = new Set();
  for (const [index, entry] of baseline.entries()) {
    if (!entry || typeof entry.method !== "string" || typeof entry.pathTemplate !== "string" || typeof entry.missingSide !== "string" || !/^(request|response(?::2\d\d)?)$/u.test(entry.missingSide)) {
      throw new Error(`Typed-contract debt baseline entry ${index} must contain method, pathTemplate, and request or response[:2xx] missingSide.`);
    }
    const key = typedContractDebtKey(entry);
    if (keys.has(key)) throw new Error(`Typed-contract debt baseline contains duplicate entry: ${key}.`);
    keys.add(key);
  }
}

export function compareOpenApiDrift(manifest, spec, baseline = []) {
  const sdkRoutes = manifestRoutes(manifest);
  const specRoutes = openApiRoutes(spec);
  const specRouteKeys = new Set(specRoutes.map((route) => comparableRouteKey(route)));
  const sdkRouteKeys = new Set(sdkRoutes.map((route) => comparableRouteKey(route)));
  const missingSdkRoutes = sdkRoutes
    .filter((route) => !specRouteKeys.has(comparableRouteKey(route)))
    .map((route) => ({
      route,
      availableMethods: specRoutes
        .filter((specRoute) => comparablePath(specRoute.pathTemplate) === comparablePath(route.pathTemplate))
        .map((specRoute) => specRoute.method)
        .sort(),
    }));

  const uncoveredSpecRoutes = specRoutes.filter(
    (route) => !sdkRouteKeys.has(comparableRouteKey(route)),
  );

  const typedContractViolations = sdkRoutes.flatMap((route) => {
    const operation = operationForRoute(spec, route);
    const violation = operation === undefined ? undefined : typedContractViolation(route, operation);
    return violation === undefined ? [] : [{ route, ...violation }];
  });
  const baselineKeys = new Set(baseline.map(typedContractDebtKey));
  const violationKeys = new Set(typedContractViolations.map(typedContractDebtKey));

  return {
    coveredSdkRouteCount: sdkRoutes.length - missingSdkRoutes.length,
    missingSdkRoutes,
    sdkRouteCount: sdkRoutes.length,
    specRouteCount: specRoutes.length,
    uncoveredSpecRoutes,
    typedContractViolations,
    staleTypedContractBaseline: baseline.filter((entry) => !violationKeys.has(typedContractDebtKey(entry))),
    unexpectedTypedContractViolations: typedContractViolations.filter((entry) => !baselineKeys.has(typedContractDebtKey(entry))),
  };
}

function operationForRoute(spec, route) {
  for (const [pathTemplate, pathItem] of Object.entries(spec.paths ?? {})) {
    if (comparablePath(pathTemplate) === comparablePath(route.pathTemplate)) {
      return pathItem?.[route.method.toLowerCase()];
    }
  }
  return undefined;
}

export function typedContractViolation(route, operation) {
  const jsonRequest = operation.requestBody?.content?.["application/json"]?.schema;

  if (route.requestType !== undefined && (jsonRequest === undefined || isJsonElement(jsonRequest))) {
    return { missingSide: "request", message: `${route.method} ${route.pathTemplate} declares ${route.requestType} without a published JSON request schema.` };
  }

  if (route.responseType !== undefined && route.responseType !== "void" && route.responseType !== "never") {
    const successes = Object.entries(operation.responses ?? {}).filter(([status]) => /^2\d\d$/u.test(status));
    if (successes.length === 0) {
      return { missingSide: "response", message: `${route.method} ${route.pathTemplate} declares ${route.responseType} without a published 2xx JSON response schema.` };
    }
    for (const [status, response] of successes) {
      const schema = response.content?.["application/json"]?.schema;
      if (schema === undefined || isJsonElement(schema)) {
        return { missingSide: `response:${status}`, message: `${route.method} ${route.pathTemplate} declares ${route.responseType} but ${status} has no published typed JSON response schema.` };
      }
    }
  }

  return undefined;
}

function typedContractDebtKey(entry) {
  const route = entry.route ?? entry;
  return `${route.method} ${route.pathTemplate} ${entry.missingSide}`;
}

function isJsonElement(schema) {
  return schema?.$ref === "#/components/schemas/JsonElement";
}

export function exitCodeForOpenApiDriftResult(result) {
  return result.missingSdkRoutes.length === 0 && result.unexpectedTypedContractViolations.length === 0 && result.staleTypedContractBaseline.length === 0 && (!result.strict || result.typedContractViolations.length === 0) ? 0 : 1;
}

export function formatOpenApiDriftResult(result, specReference, manifestReference) {
  const status = exitCodeForOpenApiDriftResult(result) === 0 ? "passed" : "failed";
  const specSnapshotLines = formatSpecSnapshotLines(result.specSnapshot);
  const stdout = [
    `OpenAPI drift check ${status}: ${result.coveredSdkRouteCount}/${result.sdkRouteCount} SDK routes covered by ${specReference}.`,
    `Manifest: ${manifestReference}`,
    `Typed contract debt baseline: ${result.typedContractViolations.length} entries (blocks production qualification).`,
  ];
  const stderr = [];

  if (result.missingSdkRoutes.length > 0) {
    stderr.push("");
    stderr.push(`SDK routes missing from OpenAPI (drift): ${result.missingSdkRoutes.length}`);

    for (const missing of result.missingSdkRoutes) {
      const availableMethods =
        missing.availableMethods.length === 0
          ? ""
          : ` (spec has path with methods: ${missing.availableMethods.join(", ")})`;
      stderr.push(`  - ${formatSdkRoute(missing.route)}${availableMethods}`);
    }

    if (specSnapshotLines.length > 0) {
      stderr.push("");
      stderr.push("OpenAPI snapshot provenance:");
      stderr.push(...specSnapshotLines.map((line) => `  - ${line}`));
    }

    stderr.push("");
    stderr.push(
      "Triage: SDK route not in spec - either the SDK is wrong OR the snapshot lags the API; refresh via Documentation openapi-sync and re-run.",
    );
  }

  if (result.typedContractViolations.length > 0) {
    stderr.push("");
    stderr.push(`SDK routes missing published typed contracts: ${result.typedContractViolations.length}`);
    for (const { route, message } of result.typedContractViolations) {
      stderr.push(`  - ${formatSdkRoute(route)}: ${message}`);
    }
  }

  if (result.unexpectedTypedContractViolations.length > 0 || result.staleTypedContractBaseline.length > 0) {
    stderr.push("\nTyped-contract debt baseline changed; production qualification remains blocked until Platform publishes schemas and the reviewed baseline is reduced.");
  }

  if (result.missingSdkRoutes.length === 0 && specSnapshotLines.length > 0) {
    stdout.push("");
    stdout.push("OpenAPI snapshot provenance:");
    stdout.push(...specSnapshotLines.map((line) => `  - ${line}`));
  }

  stdout.push("");
  stdout.push(`Spec routes not covered by SDK (informational): ${result.uncoveredSpecRoutes.length}`);

  for (const route of result.uncoveredSpecRoutes.slice(0, uncoveredDisplayLimit)) {
    stdout.push(`  - ${formatBasicRoute(route)}`);
  }

  if (result.uncoveredSpecRoutes.length > uncoveredDisplayLimit) {
    stdout.push(
      `  - ... ${result.uncoveredSpecRoutes.length - uncoveredDisplayLimit} more uncovered spec routes`,
    );
  }

  return {
    stderr: stderr.length === 0 ? "" : `${stderr.join("\n")}\n`,
    stdout: `${stdout.join("\n")}\n`,
  };
}

function writeFormattedResult(formatted, output) {
  for (const line of formatted.stdout.trimEnd().split("\n")) {
    output.log(line);
  }

  if (formatted.stderr.length === 0) {
    return;
  }

  for (const line of formatted.stderr.trimEnd().split("\n")) {
    output.error(line);
  }
}

async function readJson(reference) {
  if (/^https?:\/\//iu.test(reference)) {
    const response = await fetch(reference, {
      headers: { accept: "application/json" },
    });

    if (!response.ok) {
      throw new Error(`GET ${reference} returned HTTP ${response.status}`);
    }

    return response.json();
  }

  const filePath = path.resolve(process.cwd(), reference);
  return JSON.parse(await readFile(filePath, "utf8"));
}

async function readSpecSnapshotInfo(reference) {
  if (/^https?:\/\//iu.test(reference)) {
    return {
      kind: "url",
      reference,
      warning: "snapshot file metadata is unavailable for URL specs",
    };
  }

  const filePath = path.resolve(process.cwd(), reference);
  const [fileStat, checkoutRoot] = await Promise.all([
    stat(filePath),
    findCheckoutRoot(path.dirname(filePath)),
  ]);
  const snapshot = {
    filePath,
    kind: "file",
    mtime: fileStat.mtime.toISOString(),
    reference,
  };

  if (checkoutRoot === undefined) {
    return {
      ...snapshot,
      warning: "checkout metadata is unavailable because no .git directory was found",
    };
  }

  const relativePath = path.relative(checkoutRoot, filePath);
  const [sha, ref, lastCommit] = await Promise.all([
    gitOutput(checkoutRoot, ["rev-parse", "HEAD"]),
    gitRef(checkoutRoot),
    gitOutput(checkoutRoot, ["log", "-1", "--format=%H%x09%cI%x09%s", "--", relativePath]),
  ]);

  return {
    ...snapshot,
    checkout: {
      ref: ref ?? "detached/unknown",
      root: checkoutRoot,
      sha: sha ?? "unavailable",
    },
    lastCommit: parseLastCommit(lastCommit),
  };
}

async function findCheckoutRoot(startDirectory) {
  let currentDirectory = startDirectory;

  while (true) {
    if (await pathExists(path.join(currentDirectory, ".git"))) {
      return currentDirectory;
    }

    const parentDirectory = path.dirname(currentDirectory);

    if (parentDirectory === currentDirectory) {
      return undefined;
    }

    currentDirectory = parentDirectory;
  }
}

async function pathExists(filePath) {
  try {
    await stat(filePath);
    return true;
  } catch (error) {
    if (error && typeof error === "object" && "code" in error && error.code === "ENOENT") {
      return false;
    }

    throw error;
  }
}

async function gitOutput(checkoutRoot, args) {
  try {
    const { stdout } = await execFileAsync("git", ["-C", checkoutRoot, ...args], {
      encoding: "utf8",
    });
    const value = stdout.trim();
    return value.length === 0 ? undefined : value;
  } catch {
    return undefined;
  }
}

async function gitRef(checkoutRoot) {
  return (
    await gitOutput(checkoutRoot, ["symbolic-ref", "--quiet", "--short", "HEAD"])
  ) ?? firstLine(
    await gitOutput(checkoutRoot, [
      "for-each-ref",
      "--contains",
      "HEAD",
      "--format=%(refname:short)",
      "refs/heads",
      "refs/remotes",
    ]),
  );
}

function firstLine(value) {
  return value?.split(/\r?\n/u).find((line) => line.trim().length > 0);
}

function parseLastCommit(value) {
  if (value === undefined) {
    return undefined;
  }

  const [sha, committedAt, ...subjectParts] = value.split("\t");

  if (!sha || !committedAt) {
    return undefined;
  }

  return {
    committedAt,
    sha,
    subject: subjectParts.join("\t"),
  };
}

function formatSpecSnapshotLines(snapshot) {
  if (snapshot === undefined) {
    return [];
  }

  if (snapshot.kind === "url") {
    return [
      `Spec reference: ${snapshot.reference}`,
      snapshot.warning,
    ].filter(Boolean);
  }

  const lines = [
    `Spec reference: ${snapshot.reference}`,
    `Snapshot file: ${snapshot.filePath} (mtime ${snapshot.mtime})`,
  ];

  if (snapshot.checkout !== undefined) {
    const label = path.basename(snapshot.checkout.root) === "Documentation"
      ? "Documentation checkout"
      : "Spec checkout";
    lines.push(
      `${label}: ${snapshot.checkout.root} (ref ${snapshot.checkout.ref}, sha ${snapshot.checkout.sha})`,
    );
  } else if (snapshot.warning !== undefined) {
    lines.push(snapshot.warning);
  }

  if (snapshot.lastCommit !== undefined) {
    const subject = snapshot.lastCommit.subject ? ` ${snapshot.lastCommit.subject}` : "";
    lines.push(
      `Snapshot last commit: ${snapshot.lastCommit.sha} ${snapshot.lastCommit.committedAt}${subject}`,
    );
  } else {
    lines.push("Snapshot last commit: unavailable");
  }

  return lines;
}

function manifestRoutes(manifest) {
  if (!manifest || !Array.isArray(manifest.routes)) {
    throw new Error("SDK route manifest must contain a routes array.");
  }

  return manifest.routes.map((route, index) => {
    if (
      typeof route.method !== "string" ||
      typeof route.pathTemplate !== "string" ||
      typeof route.routeName !== "string"
    ) {
      throw new Error(`SDK route manifest entry ${index} is missing method, pathTemplate, or routeName.`);
    }

    return {
      base: typeof route.base === "string" ? route.base : undefined,
      method: route.method.toUpperCase(),
      pathTemplate: normalizePathTemplate(route.pathTemplate),
      requestType: typeof route.requestType === "string" ? route.requestType : undefined,
      responseType: typeof route.responseType === "string" ? route.responseType : undefined,
      routeName: route.routeName,
    };
  });
}

function openApiRoutes(spec) {
  if (!spec || typeof spec !== "object" || !spec.paths || typeof spec.paths !== "object") {
    throw new Error("OpenAPI spec must contain a paths object.");
  }

  const routes = [];

  for (const [pathTemplate, pathItem] of Object.entries(spec.paths)) {
    if (!pathItem || typeof pathItem !== "object") {
      continue;
    }

    for (const method of Object.keys(pathItem)) {
      const normalizedMethod = method.toLowerCase();
      if (!httpMethods.has(normalizedMethod)) {
        continue;
      }

      routes.push({
        method: normalizedMethod.toUpperCase(),
        pathTemplate: normalizePathTemplate(pathTemplate),
      });
    }
  }

  return routes.sort((left, right) => formatBasicRoute(left).localeCompare(formatBasicRoute(right)));
}

function comparableRouteKey(route) {
  return `${route.method.toUpperCase()} ${comparablePath(route.pathTemplate)}`;
}

function comparablePath(pathTemplate) {
  return normalizePathTemplate(pathTemplate).replace(/\{[^}]+\}/gu, "{param}");
}

function normalizePathTemplate(pathTemplate) {
  const normalized = pathTemplate.startsWith("/") ? pathTemplate : `/${pathTemplate}`;
  return normalized.length > 1 ? normalized.replace(/\/+$/u, "") : normalized;
}

function formatSdkRoute(route) {
  const base = route.base === undefined ? "" : ` base=${route.base}`;
  const request = route.requestType === undefined ? "" : ` request=${route.requestType}`;
  const response = route.responseType === undefined ? "" : ` response=${route.responseType}`;
  return `${route.method} ${route.pathTemplate} [${route.routeName}${base}${request}${response}]`;
}

function formatBasicRoute(route) {
  return `${route.method} ${route.pathTemplate}`;
}

function isMainModule() {
  return (
    process.argv[1] !== undefined &&
    import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href
  );
}
