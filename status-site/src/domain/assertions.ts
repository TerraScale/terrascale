/* Deep checks. Beyond answering, an endpoint can be required to return an
   expected status, contain a keyword, or satisfy assertions on its JSON
   body; values such as a version or replication lag can be extracted.
   Paths are dotted keys with array selectors: "status", "items[0].name",
   "regions[code=GRU].available". */

export type Operator = "eq" | "ne" | "lt" | "gt" | "exists" | "contains";

export interface Assertion {
  path: string;
  op: Operator;
  value?: unknown;
}

export interface CheckSpec {
  /** "2xx", "200", or a list such as "200,204". */
  expectStatus: string;
  /** A status something in front of the service (such as Cloudflare's bot
      protection) is known to refuse the probe with: a probe error, not an
      outage. Any other unexpected status still fails. */
  blockedStatus?: number | null;
  keyword: string | null;
  assertions: Assertion[];
  /** Named paths whose values are recorded with every sample. */
  extract: Record<string, string>;
  /** The URL reports on the component instead of serving it, so failing to
      fetch it says nothing about the component: the result is unknown. */
  indirect: boolean;
}

/** What one vantage saw. `status` is null when no response arrived. */
export interface Observation {
  status: number | null;
  error: string | null;
  body?: string;
}

export type Outcome = "ok" | "fail" | "unknown";

export interface Judgement {
  outcome: Outcome;
  error: string | null;
  values: Record<string, unknown>;
}

type Step = { key: string } | { index: number } | { field: string; equals: string };

/** Parses "regions[code=GRU].available" into steps; null when malformed. */
export function parsePath(path: string): Step[] | null {
  const steps: Step[] = [];
  const pattern = /([^.[\]]+)|\[(\d+)\]|\[([^=\]]+)=([^\]]*)\]|(\.)/gy;
  let match: RegExpExecArray | null;
  let expectKey = true;
  while (pattern.lastIndex < path.length && (match = pattern.exec(path))) {
    if (match[1] !== undefined) {
      if (!expectKey) return null;
      steps.push({ key: match[1] });
      expectKey = false;
    } else if (match[2] !== undefined) steps.push({ index: Number(match[2]) });
    else if (match[3] !== undefined) steps.push({ field: match[3], equals: match[4] ?? "" });
    else if (expectKey) return null;
    else expectKey = true;
  }
  return pattern.lastIndex === path.length && steps.length > 0 && !expectKey ? steps : null;
}

/** The value at `path`, or undefined when any step is missing. */
export function select(root: unknown, path: string): unknown {
  const steps = parsePath(path);
  if (!steps) return undefined;
  let value = root;
  for (const step of steps) {
    if ("key" in step) {
      value = typeof value === "object" && value !== null ? (value as Record<string, unknown>)[step.key] : undefined;
    } else if ("index" in step) {
      value = Array.isArray(value) ? value[step.index] : undefined;
    } else {
      value = Array.isArray(value)
        ? value.find(
            (item) =>
              typeof item === "object" &&
              item !== null &&
              String((item as Record<string, unknown>)[step.field]) === step.equals,
          )
        : undefined;
    }
    if (value === undefined) return undefined;
  }
  return value;
}

export function holds(assertion: Assertion, actual: unknown): boolean {
  switch (assertion.op) {
    case "exists":
      return actual !== undefined && actual !== null;
    case "eq":
      return JSON.stringify(actual) === JSON.stringify(assertion.value);
    case "ne":
      return JSON.stringify(actual) !== JSON.stringify(assertion.value);
    case "lt":
      return (
        typeof actual === "number" &&
        Number.isFinite(actual) &&
        typeof assertion.value === "number" &&
        Number.isFinite(assertion.value) &&
        actual < assertion.value
      );
    case "gt":
      return (
        typeof actual === "number" &&
        Number.isFinite(actual) &&
        typeof assertion.value === "number" &&
        Number.isFinite(assertion.value) &&
        actual > assertion.value
      );
    case "contains":
      return typeof actual === "string"
        ? typeof assertion.value === "string" && actual.includes(assertion.value)
        : Array.isArray(actual) && actual.some((item) => JSON.stringify(item) === JSON.stringify(assertion.value));
  }
}

/** Whether a status code is one the spec expects. */
export function statusMatches(spec: string, status: number): boolean {
  return spec
    .split(",")
    .map((part) => part.trim().toLowerCase())
    .some((part) => (/^[1-5]xx$/.test(part) ? Math.floor(status / 100) === Number(part[0]) : Number(part) === status));
}

/** Whether a check needs the response body. */
export const needsBody = (spec: CheckSpec): boolean =>
  spec.keyword !== null || spec.assertions.length > 0 || Object.keys(spec.extract).length > 0;

const describe = (assertion: Assertion): string =>
  `${assertion.path} ${assertion.op}${assertion.op === "exists" ? "" : ` ${JSON.stringify(assertion.value)}`}`;

/** Judges one vantage's observation of an endpoint. */
export function judge(spec: CheckSpec, observation: Observation): Judgement {
  const fail = (error: string): Judgement => ({ outcome: spec.indirect ? "unknown" : "fail", error, values: {} });
  if (observation.status === null) return fail(observation.error ?? "Unreachable");
  if (observation.status === spec.blockedStatus) {
    return { outcome: "unknown", error: `Probe blocked (HTTP ${observation.status})`, values: {} };
  }
  if (!statusMatches(spec.expectStatus, observation.status)) return fail(`HTTP ${observation.status}`);

  const body = observation.body ?? "";
  if (spec.keyword !== null && !body.includes(spec.keyword)) {
    return { outcome: "fail", error: "Keyword missing", values: {} };
  }
  if (spec.assertions.length === 0 && Object.keys(spec.extract).length === 0) {
    return { outcome: "ok", error: null, values: {} };
  }

  let json: unknown;
  try {
    json = JSON.parse(body);
  } catch {
    // Extraction alone never fails an endpoint; only assertions need JSON.
    return spec.assertions.length === 0 ? { outcome: "ok", error: null, values: {} } : fail("Invalid JSON");
  }
  const values: Record<string, unknown> = {};
  for (const [name, path] of Object.entries(spec.extract)) {
    const value = select(json, path);
    if (value !== undefined) values[name] = value;
  }
  // An indirect source that does not mention the component says nothing
  // about it, so the result is unknown rather than a failure.
  if (spec.indirect && spec.assertions.some((assertion) => select(json, assertion.path) === undefined)) {
    return { outcome: "unknown", error: "Not reported", values };
  }
  const broken = spec.assertions.find((assertion) => !holds(assertion, select(json, assertion.path)));
  return broken
    ? { outcome: "fail", error: `Expected ${describe(broken)}`, values }
    : { outcome: "ok", error: null, values };
}

export function isAssertion(value: unknown): value is Assertion {
  if (typeof value !== "object" || value === null) return false;
  const entry = value as Record<string, unknown>;
  if (typeof entry.path !== "string" || parsePath(entry.path) === null) return false;
  switch (entry.op) {
    case "exists":
      return true;
    case "lt":
    case "gt":
      return typeof entry.value === "number";
    case "eq":
    case "ne":
    case "contains":
      return entry.value !== undefined;
    default:
      return false;
  }
}

/** Reads a stored check spec, dropping anything malformed. */
export function parseSpec(row: {
  expectStatus: string;
  blockedStatus?: number | null;
  keyword: string | null;
  assertions: string;
  extract: string;
  indirect: number;
}): CheckSpec {
  const parse = (text: string): unknown => {
    try {
      return JSON.parse(text);
    } catch {
      return null;
    }
  };
  const assertions = parse(row.assertions);
  const extract = parse(row.extract);
  return {
    expectStatus: row.expectStatus,
    ...(row.blockedStatus !== null && row.blockedStatus !== undefined ? { blockedStatus: row.blockedStatus } : {}),
    keyword: row.keyword || null,
    assertions: Array.isArray(assertions) ? assertions.filter(isAssertion) : [],
    extract:
      typeof extract === "object" && extract !== null && !Array.isArray(extract)
        ? Object.fromEntries(
            Object.entries(extract).filter(
              (entry): entry is [string, string] => typeof entry[1] === "string" && parsePath(entry[1]) !== null,
            ),
          )
        : {},
    indirect: row.indirect === 1,
  };
}
