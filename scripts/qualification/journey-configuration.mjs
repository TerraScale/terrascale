import { nativeRegionalOrigin, nativeServiceOrigin } from "../../src/native-origin.ts";

const commonInputs = [
  "TERRABASE_TENANT",
  "TERRABASE_DATABASE",
  "TERRABASE_SHARD",
  "TERRABASE_COLLECTION",
  "TERRABASE_API_KEY",
  "TERRABASE_DENY_API_KEY",
  "TERRABASE_TEST_FIELD_ID",
];
const targetInputs = {
  native: [
    "TERRASCALE_SDK_TARBALL",
    "TERRASCALE_SDK_TARBALL_SHA256",
    "TERRABASE_SCHEMA_GENERATION",
    "TERRABASE_SCHEMA_DIGEST",
    "TERRABASE_TEST_FIELD_TYPE",
  ],
  browser: ["TERRABASE_BROWSER_DOCUMENT"],
};

function present(value) {
  return typeof value === "string" && value.trim() !== "";
}

function required(environment, name) {
  if (!present(environment[name])) {
    throw new Error(`Missing ${name}; real producer qualification is incomplete.`);
  }
  return environment[name];
}

function directOrigin(endpoint) {
  try {
    return nativeServiceOrigin(endpoint);
  } catch {
    throw new Error("Direct qualification requires a canonical regional HTTPS origin, independent HTTPS origin, or loopback HTTP origin.");
  }
}

/** Pure admission only; absent configuration is a skip, partial configuration is an error. */
export function journeyConfiguration(environment, target) {
  if (!Object.hasOwn(targetInputs, target)) throw new Error("Unknown journey target.");
  const routing = environment.TERRABASE_ROUTING_MODE ?? "atlas";
  if (routing !== "atlas" && routing !== "direct") {
    throw new Error("Routing mode must be direct or atlas.");
  }
  const names = [...commonInputs, ...targetInputs[target]];
  const configuredNames = [
    ...names,
    "TERRABASE_ROUTING_MODE",
    "TERRABASE_ENDPOINT",
    "TERRABASE_EXPECTED_REGION",
    "TERRASCALE_QUALIFICATION_ALLOCATION_ID",
  ];
  if (!configuredNames.some(name => present(environment[name]))) return undefined;

  const inputs = Object.fromEntries(names.map(name => [name, required(environment, name)]));
  if (inputs.TERRABASE_API_KEY === inputs.TERRABASE_DENY_API_KEY) {
    throw new Error("Denied-scope credential must differ from the allowed credential.");
  }
  if (target === "native") {
    if (!/^[0-9a-f]{64}$/u.test(inputs.TERRASCALE_SDK_TARBALL_SHA256)) {
      throw new Error("Artifact SHA-256 is not canonical.");
    }
    if (!["utf8", "i64"].includes(inputs.TERRABASE_TEST_FIELD_TYPE)) {
      throw new Error("Qualification schema must admit an exact utf8 or i64 field.");
    }
  }

  let endpoint;
  let nativeOrigin;
  if (routing === "direct") {
    endpoint = directOrigin(required(environment, "TERRABASE_ENDPOINT"));
    nativeOrigin = endpoint;
  } else {
    if (present(environment.TERRABASE_ENDPOINT)) {
      throw new Error("Atlas qualification must omit the provisioned endpoint to prove actual discovery.");
    }
    const region = required(environment, "TERRABASE_EXPECTED_REGION");
    try {
      nativeOrigin = nativeRegionalOrigin(region);
    } catch {
      throw new Error("Expected native region must be canonical.");
    }
    required(environment, "TERRASCALE_QUALIFICATION_ALLOCATION_ID");
  }
  return Object.freeze({
    routing,
    nativeOrigin,
    ...(endpoint === undefined ? {} : { endpoint }),
    inputs: Object.freeze(inputs),
  });
}
