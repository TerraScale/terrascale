// The native TypeScript conformance harness for the `terrascale.sync.v2`
// golden-vector corpus (SE-05's per-language harness contract, SE-09's
// implementation). The corpus is SE-04's committed vector set, vendored
// byte-exact into this repository's test fixtures together with the
// corpus integrity manifest; this module executes the step vocabulary of
// `docs/sync/terrascale-sync-v2-conformance.md` against this SDK's own
// codec and emits the versioned conformance report.
//
// Client-side scope (the kit's CI recipe): every recorded frame asserts
// `decode(wire) == frame` and `encode(frame) == wire`; every
// `decode_rejects` entry asserts the exact typed rejection; every
// `budget` step recounts from the committed bytes; the `serve_*` steps
// are asserted through their recorded frame expectations. `apply` steps
// are server-side journal commits and execute no client-side check.
//
// The module is platform-neutral on purpose (no Node-only imports): the
// host supplies the corpus loader, and SHA-256 runs on the Web Crypto
// every supported runtime provides.

import {
  decodeSyncV2Frame,
  encodeSyncV2Frame,
  sealSyncV2Frame,
  syncV2FrameBudgetBytes,
  syncV2FrameClassOf,
  syncV2PayloadLengthOf,
  syncV2Contract,
  syncV2ProtocolVersion,
  verifySyncV2Frame,
  type SyncV2Checkpoint,
  type SyncV2ErrorCodeName,
  type SyncV2Frame,
  type SyncV2FrameBody,
  type SyncV2FrameClass,
  type SyncV2ProtocolError,
  type SyncV2Value,
} from "./frame.js";

/** One corpus file: its exact bytes and decoded text. */
export type SyncV2CorpusFile = {
  readonly bytes: Uint8Array;
  readonly text: string;
};

/**
 * The corpus access surface: `read` resolves a path relative to the
 * corpus root (`manifest.json`, `vectors/<category>.json`); `list`, when
 * supplied, enumerates a directory's files so the run can prove file-set
 * agreement with the manifest.
 */
export type SyncV2Corpus = {
  read(relativePath: string): Promise<SyncV2CorpusFile>;
  list?(relativeDirectory: string): Promise<readonly string[]>;
};

/** The closed category set a conforming corpus covers exactly once. */
export const syncV2CorpusCategories: readonly string[] = [
  "fresh-session",
  "checkpoint-resume",
  "reconnect-replay",
  "slow-consumer",
  "region-route-change",
  "resync-required",
  "malformed-frame-rejection",
  "repeated-change-delta",
  "trigger-notification",
];

/** One executed check of a vector run (stable check names of the kit contract). */
export type SyncV2ConformanceFailure = {
  readonly step: number | null;
  readonly check: string;
  readonly reason: string;
};

/** One vector's result inside the conformance report. */
export type SyncV2ConformanceVector = {
  readonly category: string;
  readonly file: string;
  readonly status: "pass" | "fail";
  readonly aborted: boolean;
  readonly checks: Readonly<Record<string, number>>;
  readonly failures: readonly SyncV2ConformanceFailure[];
};

/** The versioned conformance report (`terrascale.sync.v2/conformance-report@1`). */
export type SyncV2ConformanceReport = {
  readonly schema: "terrascale.sync.v2/conformance-report@1";
  readonly protocol: typeof syncV2Contract;
  readonly protocol_version: typeof syncV2ProtocolVersion;
  readonly harness: string;
  readonly corpus: typeof syncV2Contract;
  readonly summary: {
    readonly status: "pass" | "fail";
    readonly vectors_total: number;
    readonly vectors_passed: number;
    readonly vectors_failed: number;
    readonly checks_total: number;
    readonly checks_passed: number;
    readonly checks_failed: number;
    readonly problems: readonly string[];
  };
  readonly manifest?: {
    readonly status: "verified" | "failed";
    readonly problems: readonly string[];
  };
  readonly vectors: readonly SyncV2ConformanceVector[];
};

/** The options of one conformance run. */
export type SyncV2ConformanceOptions = {
  /** The vendored corpus (the committed vectors and manifest). */
  readonly corpus: SyncV2Corpus;
  /** The harness identity the report records. */
  readonly harness?: string | undefined;
};

const manifestPath = "manifest.json";
const vectorsDirectory = "vectors";
const triggerContentFreeKeys = [
  "causation",
  "collection",
  "correlation",
  "kind",
  "operation_id",
  "storage_sequence",
];

type ManifestEntry = {
  readonly category: string;
  readonly file: string;
  readonly sha256: string;
  readonly bytes: number;
};

type Manifest = {
  readonly schema: string;
  readonly protocol: string;
  readonly protocol_version: number;
  readonly vectors: readonly ManifestEntry[];
};

class CheckFailure extends Error {
  public constructor(public readonly check: string, reason: string) {
    super(reason);
  }
}

class VectorRun {
  public readonly checks = new Map<string, number>();
  public readonly failures: SyncV2ConformanceFailure[] = [];
  public aborted = false;

  public check(check: string, step: number | null, execute: () => void): boolean {
    this.checks.set(check, (this.checks.get(check) ?? 0) + 1);
    try {
      execute();
    } catch (error) {
      if (!(error instanceof CheckFailure)) {
        throw error;
      }
      this.failures.push({ step, check, reason: error.message });
      return false;
    }
    return true;
  }
}

/** Runs the vendored golden-vector corpus against this SDK's codec. */
export async function runSyncV2Conformance(
  options: SyncV2ConformanceOptions,
): Promise<SyncV2ConformanceReport> {
  const harness = options.harness ?? "@terrascale/sdk";
  const problems: string[] = [];
  const manifestProblems: string[] = [];

  const manifestFile = await options.corpus.read(manifestPath);
  const manifest = parseManifest(manifestFile.text);
  if (manifest === null) {
    manifestProblems.push("the manifest does not parse");
  }

  const listedFiles = await listVectors(options.corpus);
  if (listedFiles === null) {
    problems.push("the corpus directory cannot be enumerated; the manifest alone gates the file set");
  }

  const expectedFiles = manifest
    ? [...manifest.vectors].map((entry) => entry.file).sort()
    : [];
  if (listedFiles !== null) {
    const expectedSet = new Set(expectedFiles);
    const listedSet = new Set(listedFiles);
    for (const file of listedFiles) {
      if (!expectedSet.has(file)) {
        problems.push(`the corpus file ${file} has no manifest entry`);
      }
    }
    for (const file of expectedFiles) {
      if (!listedSet.has(file)) {
        problems.push(`the manifest entry ${file} has no corpus file`);
      }
    }
  }

  const sha256Of = async (bytes: Uint8Array): Promise<string> => {
    const digest = await globalThis.crypto.subtle.digest("SHA-256", bytes as unknown as ArrayBuffer);
    return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
  };

  const manifestEntriesByFile = new Map(manifest?.vectors.map((entry) => [entry.file, entry]) ?? []);
  // Vectors execute over the manifest's file set; the listing only proves
  // set agreement, so an unregistered file is a problem, never a read.
  const files = (expectedFiles.length > 0 ? expectedFiles : listedFiles ?? []).slice().sort();
  const vectors: SyncV2ConformanceVector[] = [];
  const seenCategories = new Set<string>();

  for (const file of files) {
    const relativePath = `${vectorsDirectory}/${file}`;
    const entry = manifestEntriesByFile.get(file);
    let corpusText: string;
    try {
      const loaded = await options.corpus.read(relativePath);
      if (entry !== undefined) {
        const sha256 = await sha256Of(loaded.bytes);
        if (sha256 !== entry.sha256 || loaded.bytes.length !== entry.bytes) {
          manifestProblems.push(
            `the corpus file ${file} disagrees with its manifest entry (digest or size)`,
          );
        }
      }
      corpusText = loaded.text;
    } catch {
      manifestProblems.push(`the corpus file ${file} cannot be read`);
      vectors.push({
        category: entry?.category ?? "unknown",
        file,
        status: "fail",
        aborted: true,
        checks: {},
        failures: [{ step: null, check: "manifest_entry_matches", reason: "the corpus file cannot be read" }],
      });
      continue;
    }

    const vector = runVector(file, corpusText);
    vectors.push(vector);
    if (vector.status === "pass" && entry !== undefined) {
      seenCategories.add(entry.category);
    } else if (vector.status === "pass" && entry === undefined) {
      const parsed = JSON.parse(corpusText) as { category?: unknown };
      if (typeof parsed.category === "string") {
        seenCategories.add(parsed.category);
      }
    }
  }

  for (const category of syncV2CorpusCategories) {
    if (!seenCategories.has(category)) {
      problems.push(`the corpus is missing the ${category} category`);
    }
  }

  const vectorsPassed = vectors.filter((vector) => vector.status === "pass").length;
  const vectorsFailed = vectors.length - vectorsPassed;
  const checksTotal = vectors.reduce(
    (total, vector) => total + Object.values(vector.checks).reduce((sum, count) => sum + count, 0),
    0,
  );
  const checksFailed = vectors.reduce((total, vector) => total + vector.failures.length, 0);
  const status = vectorsFailed === 0 && problems.length === 0 && manifestProblems.length === 0
    ? ("pass" as const)
    : ("fail" as const);

  const report: SyncV2ConformanceReport = {
    schema: "terrascale.sync.v2/conformance-report@1",
    protocol: syncV2Contract,
    protocol_version: syncV2ProtocolVersion,
    harness,
    corpus: syncV2Contract,
    summary: {
      status,
      vectors_total: vectors.length,
      vectors_passed: vectorsPassed,
      vectors_failed: vectorsFailed,
      checks_total: checksTotal,
      checks_passed: checksTotal - checksFailed,
      checks_failed: checksFailed,
      problems: [...problems, ...manifestProblems],
    },
    ...(manifestProblems.length === 0
      ? { manifest: { status: "verified" as const, problems: [] } }
      : { manifest: { status: "failed" as const, problems: manifestProblems } }),
    vectors,
  };
  return report;
}

async function listVectors(corpus: SyncV2Corpus): Promise<readonly string[] | null> {
  if (corpus.list === undefined) {
    return null;
  }
  return corpus.list(vectorsDirectory);
}

function parseManifest(text: string): Manifest | null {
  const value = JSON.parse(text) as Partial<Manifest> | null;
  if (
    value === null
    || value.schema !== "terrascale.sync.v2/corpus-manifest@1"
    || value.protocol !== syncV2Contract
    || value.protocol_version !== syncV2ProtocolVersion
    || !Array.isArray(value.vectors)
  ) {
    return null;
  }
  return value as Manifest;
}

function runVector(file: string, text: string): SyncV2ConformanceVector {
  const run = new VectorRun();
  let corpus: Record<string, unknown>;
  let steps: unknown[];
  try {
    const parsed = JSON.parse(text) as Record<string, unknown>;
    corpus = parsed;
    steps = Array.isArray(parsed.steps) ? parsed.steps : [];
  } catch (error) {
    run.failures.push({
      step: null,
      check: "corpus_schema_identity",
      reason: `the vector file does not parse: ${error instanceof Error ? error.message : "unknown"}`,
    });
    return finish(file, "malformed", run);
  }

  run.check("corpus_schema_identity", null, () => {
    if (corpus.schema !== "terrascale.sync.v2/vector-corpus@1") {
      throw new CheckFailure("corpus_schema_identity", "the schema identity is not vector-corpus@1");
    }
    if (corpus.protocol !== syncV2Contract || corpus.protocol_version !== syncV2ProtocolVersion) {
      throw new CheckFailure("corpus_schema_identity", "the protocol identity disagrees");
    }
  });

  for (const [index, rawStep] of steps.entries()) {
    if (run.aborted) {
      break;
    }
    executeStep(run, index, rawStep);
  }

  return finish(file, typeof corpus.category === "string" ? corpus.category : "unknown", run);
}

function finish(file: string, category: string, run: VectorRun): SyncV2ConformanceVector {
  return {
    category,
    file,
    status: run.failures.length === 0 ? "pass" : "fail",
    aborted: run.aborted,
    checks: Object.fromEntries([...run.checks.entries()].sort(([a], [b]) => (a < b ? -1 : 1))),
    failures: run.failures,
  };
}

const executableKeys: readonly string[] = [
  "apply",
  "client_frame",
  "server_frame",
  "serve_delta",
  "serve_snapshot",
  "serve_trigger",
  "decode_rejects",
  "budget",
];

function executeStep(run: VectorRun, index: number, rawStep: unknown): void {
  const step = objectOf(rawStep, "step");
  const present = executableKeys.filter((key) => key in step && step[key] !== undefined);
  if (present.length > 1) {
    run.check("step_kind_known", index, () => {
      throw new CheckFailure("step_kind_known", "a step carries more than one executable key");
    });
    return;
  }
  if (present.length === 0) {
    if (!("note" in step)) {
      run.check("step_kind_known", index, () => {
        throw new CheckFailure("step_kind_known", "the step carries no executable key and no note");
      });
    }
    return;
  }
  const key = present[0]!;
  switch (key) {
    case "apply":
      // A server-side journal commit; the client contract has no check.
      return;
    case "client_frame":
    case "server_frame":
      checkRecordedFrame(run, index, objectOf(step[key], key));
      return;
    case "serve_delta":
      checkServeDelta(run, index, objectOf(step[key], key));
      return;
    case "serve_snapshot":
      checkServeSnapshot(run, index, objectOf(step[key], key));
      return;
    case "serve_trigger":
      checkServeTrigger(run, index, objectOf(step[key], key));
      return;
    case "decode_rejects":
      checkDecodeRejects(run, index, objectOf(step[key], key));
      return;
    case "budget":
      checkBudget(run, index, objectOf(step[key], key));
      return;
  }
}

function checkRecordedFrame(run: VectorRun, step: number, entry: Record<string, unknown>): void {
  const sealed = new (class {
    public frame: SyncV2Frame | undefined;
  })();
  run.check("frame_description_seals", step, () => {
    sealed.frame = frameFromDescription(objectOf(entry.frame, "frame"));
  });
  if (sealed.frame === undefined) {
    run.aborted = true;
    return;
  }
  const wire = hexToBytes(stringOf(entry.wire, "wire"));
  run.check("frame_encode_bytes_exact", step, () => {
    if (!bytesEqual(encodeSyncV2Frame(sealed.frame!), wire)) {
      throw new CheckFailure("frame_encode_bytes_exact", "the structured frame does not encode to the recorded bytes");
    }
  });
  const decoded = new (class {
    public frame: SyncV2Frame | undefined;
  })();
  run.check("frame_decode_roundtrip", step, () => {
    decoded.frame = decodeSyncV2Frame(wire);
    if (decoded.frame.body.kind !== sealed.frame!.body.kind) {
      throw new CheckFailure("frame_decode_roundtrip", "the decoded kind disagrees with the description");
    }
    if (!frameEqualsDescription(decoded.frame, objectOf(entry.frame, "frame"))) {
      throw new CheckFailure("frame_decode_roundtrip", "the decoded frame disagrees with the structured description");
    }
  });
  if (decoded.frame === undefined) {
    run.aborted = true;
    return;
  }
  run.check("frame_digest_verifies", step, () => {
    if (!verifySyncV2Frame(decoded.frame!)) {
      throw new CheckFailure("frame_digest_verifies", "the recorded frame's digest does not verify");
    }
  });
  run.check("frame_envelope_identity", step, () => {
    const description = objectOf(entry.frame, "frame");
    if (
      decoded.frame!.correlation !== bigOf(description.correlation)
      || decoded.frame!.causation !== bigOf(description.causation ?? 0)
      || decoded.frame!.idempotency !== (typeof description.idempotency === "string" ? description.idempotency : undefined)
    ) {
      throw new CheckFailure("frame_envelope_identity", "correlation, causation, or idempotency disagree");
    }
  });
}

function checkServeDelta(run: VectorRun, step: number, entry: Record<string, unknown>): void {
  run.check("serve_completes", step, () => {
    checkpointFromDescription(entry.from ?? null);
  });
  const wireHex = typeof entry.expect_wire === "string" ? entry.expect_wire : null;
  if (wireHex === null) {
    return;
  }
  const wire = hexToBytes(wireHex);
  const described = entry.expect_frame === undefined ? null : objectOf(entry.expect_frame, "expect_frame");
  run.check("serve_frame_bytes_exact", step, () => {
    const frame = frameFromDescription(described === null ? {} : described);
    if (!bytesEqual(encodeSyncV2Frame(frame), wire)) {
      throw new CheckFailure("serve_frame_bytes_exact", "the described answer does not encode to the recorded bytes");
    }
  });
  run.check("serve_frame_matches_description", step, () => {
    const decoded = decodeSyncV2Frame(wire);
    if (described === null || !frameEqualsDescription(decoded, described)) {
      throw new CheckFailure("serve_frame_matches_description", "the recorded bytes disagree with the description");
    }
  });
}

function checkServeSnapshot(run: VectorRun, step: number, entry: Record<string, unknown>): void {
  run.check("serve_completes", step, () => {
    bigOf(entry.correlation ?? 0);
  });
  const wire = hexToBytes(stringOf(entry.expect_wire, "expect_wire"));
  const described = objectOf(entry.expect_frame, "expect_frame");
  run.check("serve_frame_bytes_exact", step, () => {
    const frame = frameFromDescription(described);
    if (!bytesEqual(encodeSyncV2Frame(frame), wire)) {
      throw new CheckFailure("serve_frame_bytes_exact", "the described snapshot does not encode to the recorded bytes");
    }
  });
  run.check("serve_frame_matches_description", step, () => {
    const decoded = decodeSyncV2Frame(wire);
    if (!frameEqualsDescription(decoded, described)) {
      throw new CheckFailure("serve_frame_matches_description", "the recorded bytes disagree with the description");
    }
  });
}

function checkServeTrigger(run: VectorRun, step: number, entry: Record<string, unknown>): void {
  run.check("serve_completes", step, () => {
    frameFromDescription({
      kind: "trigger",
      correlation: entry.correlation,
      causation: entry.causation ?? 0,
      collection: entry.collection,
      storage_sequence: entry.sequence,
      operation_id: entry.operation_id,
    });
  });
  const wire = hexToBytes(stringOf(entry.expect_wire, "expect_wire"));
  const described = objectOf(entry.expect_frame, "expect_frame");
  run.check("serve_frame_bytes_exact", step, () => {
    const frame = frameFromDescription(described);
    if (!bytesEqual(encodeSyncV2Frame(frame), wire)) {
      throw new CheckFailure("serve_frame_bytes_exact", "the described trigger does not encode to the recorded bytes");
    }
  });
  run.check("trigger_content_free", step, () => {
    const keys = Object.keys(described).sort();
    if (keys.join(",") !== triggerContentFreeKeys.join(",")) {
      throw new CheckFailure(
        "trigger_content_free",
        `the trigger description must be exactly ${triggerContentFreeKeys.join(", ")}`,
      );
    }
  });
  run.check("trigger_within_budget", step, () => {
    if (wire.length > syncV2FrameBudgetBytes.trigger) {
      throw new CheckFailure("trigger_within_budget", "the trigger exceeds its 256-byte budget");
    }
  });
}

function checkDecodeRejects(run: VectorRun, step: number, entry: Record<string, unknown>): void {
  run.check("decode_rejects_typed_error", step, () => {
    const wire = hexToBytes(stringOf(entry.wire, "wire"));
    const expected = stringOf(entry.error, "error");
    let code: string;
    try {
      decodeSyncV2Frame(wire);
      throw new CheckFailure("decode_rejects_typed_error", "the bytes decoded but must be rejected");
    } catch (error) {
      if (error instanceof CheckFailure) {
        throw error;
      }
      const typed = error as SyncV2ProtocolError;
      code = typed.code;
    }
    if (code !== expected) {
      throw new CheckFailure("decode_rejects_typed_error", `expected ${expected}, got ${code}`);
    }
  });
}

function checkBudget(run: VectorRun, step: number, entry: Record<string, unknown>): void {
  const wire = hexToBytes(stringOf(entry.wire, "wire"));
  const className = stringOf(entry.class, "class");
  const decoded = new (class {
    public frame: SyncV2Frame | undefined;
  })();
  run.check("budget_frame_decodes", step, () => {
    decoded.frame = decodeSyncV2Frame(wire);
  });
  if (decoded.frame === undefined) {
    run.aborted = true;
    return;
  }
  run.check("budget_class_matches", step, () => {
    if (syncV2FrameClassOf(decoded.frame!.body.kind) !== (className as SyncV2FrameClass)) {
      throw new CheckFailure("budget_class_matches", `the frame's class is not ${className}`);
    }
  });
  run.check("budget_within_budget", step, () => {
    const body = decoded.frame!.body;
    const oversizeException = body.kind === "delta_batch" && body.single_commit_oversize && body.commits === 1n;
    if (wire.length > syncV2FrameBudgetBytes[className as SyncV2FrameClass] && !oversizeException) {
      throw new CheckFailure("budget_within_budget", `${wire.length} bytes exceed the ${className} budget`);
    }
  });
  if (entry.payload_bytes !== undefined) {
    run.check("budget_payload_accounting", step, () => {
      const payload = syncV2PayloadLengthOf(decoded.frame!.body);
      if (payload !== numberOrBig(entry.payload_bytes)) {
        throw new CheckFailure("budget_payload_accounting", "the payload byte count disagrees");
      }
      if (wire.length - payload !== numberOrBig(entry.envelope_overhead_bytes)) {
        throw new CheckFailure("budget_payload_accounting", "the per-frame envelope overhead disagrees");
      }
    });
  }
  if (entry.bytes_per_delivered_change !== undefined) {
    run.check("budget_bytes_per_delivered_change", step, () => {
      const body = decoded.frame!.body;
      if (body.kind !== "delta_batch") {
        throw new CheckFailure("budget_bytes_per_delivered_change", "bytes-per-change applies to delta batches");
      }
      if (body.commits === 0n) {
        throw new CheckFailure("budget_bytes_per_delivered_change", "the batch carries no changes");
      }
      if (Math.floor(wire.length / Number(body.commits)) !== numberOrBig(entry.bytes_per_delivered_change)) {
        throw new CheckFailure("budget_bytes_per_delivered_change", "the bytes per delivered change disagree");
      }
    });
  }
  if (entry.delta_bytes !== undefined && entry.snapshot_bytes !== undefined) {
    run.check("budget_delta_below_snapshot", step, () => {
      if (!(numberOrBig(entry.delta_bytes) < numberOrBig(entry.snapshot_bytes))) {
        throw new CheckFailure("budget_delta_below_snapshot", "the delta path is not below the snapshot re-send");
      }
    });
  }
}

// --- Corpus description parsing ----------------------------------------------

function frameFromDescription(value: Record<string, unknown>): SyncV2Frame {
  const kind = stringOf(value.kind, "kind");
  const correlation = bigOf(value.correlation ?? 0);
  const causation = bigOf(value.causation ?? 0);
  const idempotency = typeof value.idempotency === "string" ? value.idempotency : undefined;
  const body = bodyFromDescription(kind, value);
  return sealSyncV2Frame(body, correlation, causation, idempotency);
}

function bodyFromDescription(kind: string, value: Record<string, unknown>): SyncV2FrameBody {
  switch (kind) {
    case "session_setup":
      return {
        kind,
        collections: stringsOf(value.collections),
        resume: value.resume === null || value.resume === undefined
          ? null
          : checkpointFromDescription(value.resume),
      };
    case "session_accepted":
      return {
        kind,
        session: bigOf(value.session),
        server_checkpoint: checkpointFromDescription(value.server_checkpoint),
        collections: stringsOf(value.collections),
      };
    case "subscription_modify":
      return { kind, add: stringsOf(value.add), remove: stringsOf(value.remove) };
    case "subscription_changed":
      return { kind, active: stringsOf(value.active) };
    case "checkpoint_ack":
      return { kind, checkpoint: checkpointFromDescription(value.checkpoint) };
    case "heartbeat":
      return { kind };
    case "delta_batch":
      return {
        kind,
        codec: "loro-delta/1",
        payload: stringOf(value.payload, "payload"),
        next: checkpointFromDescription(value.next),
        within_budget: booleanOf(value.within_budget),
        single_commit_oversize: booleanOf(value.single_commit_oversize),
        commits: bigOf(value.commits),
      };
    case "snapshot_answer":
      return {
        kind,
        codec: "loro-snapshot/1",
        payload: stringOf(value.payload, "payload"),
        checkpoint: checkpointFromDescription(value.checkpoint),
      };
    case "trigger":
      return {
        kind,
        collection: stringOf(value.collection, "collection"),
        storage_sequence: bigOf(value.storage_sequence),
        operation_id: stringOf(value.operation_id, "operation_id"),
      };
    case "resync_required":
      return {
        kind,
        floor: checkpointFromDescription(value.floor),
        reason: value.reason === "history-compacted" ? "history-compacted" : "behind-retained-floor",
      };
    case "region_route_change":
      return { kind, region: stringOf(value.region, "region") };
    case "error":
      return {
        kind,
        code: stringOf(value.code, "code") as SyncV2ErrorCodeName,
        detail: value.detail === undefined || value.detail === null ? null : stringOf(value.detail, "detail"),
      };
    case "mutation_request": {
      if (value.put !== undefined && value.put !== null) {
        const put: Record<string, SyncV2Value> = {};
        for (const [field, raw] of Object.entries(objectOf(value.put, "put"))) {
          put[field] = valueFromDescription(raw);
        }
        return { kind, collection: stringOf(value.collection, "collection"), document: stringOf(value.document, "document"), put };
      }
      return { kind, collection: stringOf(value.collection, "collection"), document: stringOf(value.document, "document"), delete: true };
    }
    case "mutation_ack": {
      if (value.outcome === "applied") {
        return {
          kind,
          outcome: "applied",
          storage_sequence: bigOf(value.storage_sequence),
          operation_id: stringOf(value.operation_id, "operation_id"),
        };
      }
      return {
        kind,
        outcome: "rejected",
        rejection: stringOf(value.rejection, "rejection") as SyncV2ErrorCodeName,
      };
    }
    default:
      throw malformed("unknown frame kind");
  }
}

function checkpointFromDescription(value: unknown): SyncV2Checkpoint {
  if (value === null || value === undefined) {
    return { ends: [], storage_sequence: 0n };
  }
  const checkpoint = objectOf(value, "checkpoint");
  const ends = (Array.isArray(checkpoint.ends) ? checkpoint.ends : []).map((pair) => {
    const entry = Array.isArray(pair) ? pair : [];
    return [bigOf(entry[0] ?? 0), numberOrBig(entry[1] ?? 0)] as const;
  });
  return { ends, storage_sequence: bigOf(checkpoint.storage_sequence ?? 0) };
}

function valueFromDescription(value: unknown): SyncV2Value {
  if (value === null || typeof value === "boolean" || typeof value === "string" || typeof value === "number") {
    return value;
  }
  throw malformed("unsupported vector value");
}

function frameEqualsDescription(frame: SyncV2Frame, description: Record<string, unknown>): boolean {
  if (frame.body.kind !== description.kind) {
    return false;
  }
  if (frame.correlation !== bigOf(description.correlation ?? 0)) {
    return false;
  }
  if (frame.causation !== bigOf(description.causation ?? 0)) {
    return false;
  }
  const expectedIdempotency = typeof description.idempotency === "string" ? description.idempotency : undefined;
  if (frame.idempotency !== expectedIdempotency) {
    return false;
  }
  const expected = bodyFromDescription(frame.body.kind, description);
  return deepEqualTolerant(frame.body, expected);
}

function deepEqualTolerant(left: unknown, right: unknown): boolean {
  if (typeof left === "bigint" || typeof right === "bigint" || typeof left === "number" || typeof right === "number") {
    if ((typeof left === "bigint" || typeof left === "number") && (typeof right === "bigint" || typeof right === "number")) {
      return BigInt(left) === BigInt(right);
    }
    return left === right;
  }
  if (left === right) {
    return true;
  }
  if (Array.isArray(left) && Array.isArray(right)) {
    return left.length === right.length && left.every((item, index) => deepEqualTolerant(item, right[index]));
  }
  if (typeof left === "object" && typeof right === "object" && left !== null && right !== null) {
    const leftKeys = Object.keys(left).sort();
    const rightKeys = Object.keys(right).sort();
    if (leftKeys.join(" ") !== rightKeys.join(" ")) {
      return false;
    }
    return leftKeys.every((key) =>
      deepEqualTolerant((left as Record<string, unknown>)[key], (right as Record<string, unknown>)[key]),
    );
  }
  return false;
}

// --- Small typed accessors ----------------------------------------------------

function malformed(reason: string): Error {
  return new CheckFailure("frame_description_seals", reason);
}

function objectOf(value: unknown, path: string): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw malformed(`${path} must be an object`);
  }
  return value as Record<string, unknown>;
}

function stringOf(value: unknown, path: string): string {
  if (typeof value !== "string") {
    throw malformed(`${path} must be a string`);
  }
  return value;
}

function booleanOf(value: unknown): boolean {
  return value === true;
}

function bigOf(value: unknown): bigint {
  if (typeof value === "bigint") {
    return value;
  }
  if (typeof value === "number" && Number.isInteger(value)) {
    return BigInt(value);
  }
  if (typeof value === "string" && /^-?\d+$/u.test(value)) {
    return BigInt(value);
  }
  throw malformed("the value is not an integer");
}

function numberOrBig(value: unknown): number {
  if (typeof value === "number") {
    return value;
  }
  if (typeof value === "bigint") {
    return Number(value);
  }
  if (typeof value === "string" && /^-?\d+$/u.test(value)) {
    return Number(value);
  }
  throw malformed("the value is not a number");
}

function stringsOf(value: unknown): string[] {
  if (!Array.isArray(value)) {
    throw malformed("the value must be a string array");
  }
  return value.map((entry) => stringOf(entry, "entry"));
}

function hexToBytes(hex: string): Uint8Array {
  const bytes = new Uint8Array(hex.length / 2);
  for (let index = 0; index < bytes.length; index++) {
    bytes[index] = Number.parseInt(hex.slice(index * 2, index * 2 + 2), 16);
  }
  return bytes;
}

function bytesEqual(left: Uint8Array, right: Uint8Array): boolean {
  return left.length === right.length && left.every((byte, index) => byte === right[index]);
}
