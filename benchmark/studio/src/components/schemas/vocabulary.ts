// Shared vocabulary for the schemas page (SM-04,
// TerraBase/TerraBaseV2#28): the closed Schema Migration state set and the
// validation modes mirrored from the engine (`MigrationStatus`,
// src/families/migrations/record.rs) and the TBEP registry
// (`migration_state_kind`), typed state parsing for the contract's
// `SchemaStateResult`, the collection-bound read discipline, and the named
// DB-04 live-update seam this page deliberately does not build. Client-safe
// by construction — the only generated-client imports are types — so the
// server routes and any later live island speak exactly one vocabulary.
//
// The labels below are the engine's public variant docs verbatim
// (src/families/migrations/record.rs, guarded by tests/schemas-page.test.mts)
// — the page never paraphrases migration semantics.

import type { SchemaStateResult } from "@/api/generated/client";

/** The closed Schema Migration state set, engine `MigrationStatus` order. */
export const MIGRATION_STATE_KINDS = ["none", "in_progress", "rolling_back", "complete"] as const;

export type MigrationStateKindWire = (typeof MIGRATION_STATE_KINDS)[number];

/** The recorded-policy vocabulary: a collection enforces a schema or is free. */
export const VALIDATION_MODES = ["enforced", "none"] as const;

export type ValidationModeWire = (typeof VALIDATION_MODES)[number];

/** `MigrationStatus::None` doc (src/families/migrations/record.rs). */
export const MIGRATION_STATE_LABELS: Record<MigrationStateKindWire, string> = {
  none: "No migration record exists for the collection.",
  in_progress: "A forward drive is applying pages.",
  rolling_back: "A rollback drive is restoring pages.",
  complete: "A migration completed with the recorded receipt.",
};

/** How many collections one page load reads the schema state of (bounded). */
export const SCHEMA_COLLECTIONS_BOUND = 32;

/** One migration drive's public facts (the `MigrationStatus` payload fields). */
export interface MigrationDriveView {
  readonly manifest: string;
  readonly fromVersion: number | null;
  readonly toVersion: number | null;
  readonly documents: number | null;
  readonly transformed: number | null;
  readonly pages: number | null;
}

/** The parsed `SchemaStateResult` of one collection. */
export interface SchemaStateView {
  readonly collection: string;
  readonly enforcedVersion: number | null;
  readonly ruleCount: number;
  readonly validation: ValidationModeWire;
  readonly migrationKind: MigrationStateKindWire;
  readonly migration: MigrationDriveView | null;
}

export type SchemaStateParse =
  | { readonly kind: "recognized"; readonly state: SchemaStateView; readonly malformed: number }
  | { readonly kind: "unrecognized" };

/**
 * The DB-04 live-update seam: the typed shape this page will subscribe
 * through once DB-04's subscription pattern carries schema-state deltas.
 * SM-04 deliberately builds no transport of its own — a sibling packet owns
 * the SSE surface — so the page renders this seam's landing state honestly
 * instead of shipping a second feed.
 */
export interface SchemaLiveSeam {
  /** The ticket that owns the live subscription surface. */
  readonly owner: "DB-04";
  /** What will carry the deltas: the contract's subscription discipline. */
  readonly carrier: "contract subscriptions (SSE)";
  /** Whether that carrier serves schema-state deltas today. */
  readonly landed: false;
  /** The honest sentence the page renders next to the seam. */
  readonly note: string;
}

/** The seam record the page renders (see {@link SchemaLiveSeam}). */
export const SCHEMA_LIVE_SEAM: SchemaLiveSeam = {
  owner: "DB-04",
  carrier: "contract subscriptions (SSE)",
  landed: false,
  note: "This page is a typed read per navigation. Live schema-state updates land with DB-04's subscription pattern; SM-04 builds no feed of its own.",
};

/** The short label the panel carries for the seam. */
export const SCHEMA_LIVE_SEAM_LABEL = "Live updates: not yet (DB-04 seam)";

function isJsonObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isNonNegativeInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= 0;
}

function isPositiveInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value > 0;
}

/** Reads one optional non-negative integer fact, or null when absent/malformed. */
function optionalCount(value: unknown): { value: number | null; malformed: boolean } {
  if (value === undefined || value === null) {
    return { value: null, malformed: false };
  }
  if (isNonNegativeInteger(value)) {
    return { value, malformed: false };
  }
  return { value: null, malformed: true };
}

/**
 * Parses one `schema.status` result. Anything that is not the contract's
 * shape — a missing identity, an unknown validation mode or migration state,
 * an enforced schema without a version — is unrecognized, never coerced;
 * optional drive facts that arrive malformed are counted and rendered as
 * absent, never silently dropped.
 */
export function parseSchemaStateResult(result: SchemaStateResult | unknown): SchemaStateParse {
  if (!isJsonObject(result)) {
    return { kind: "unrecognized" };
  }
  const collection = result.collection;
  const validation = result.validation;
  const migrationKind = result.migrationKind;
  if (
    typeof collection !== "string" ||
    collection.length === 0 ||
    !VALIDATION_MODES.includes(validation as ValidationModeWire) ||
    !MIGRATION_STATE_KINDS.includes(migrationKind as MigrationStateKindWire) ||
    !isNonNegativeInteger(result.ruleCount)
  ) {
    return { kind: "unrecognized" };
  }

  let malformed = 0;
  const enforcedRaw = result.enforcedVersion;
  if (validation === "enforced") {
    if (!isPositiveInteger(enforcedRaw)) {
      return { kind: "unrecognized" };
    }
  } else if (enforcedRaw !== undefined && enforcedRaw !== null) {
    return { kind: "unrecognized" };
  }

  let migration: MigrationDriveView | null = null;
  if (migrationKind !== "none") {
    const manifest = result.migrationManifest;
    if (typeof manifest !== "string" || manifest.length === 0) {
      return { kind: "unrecognized" };
    }
    const from = optionalCount(result.migrationFromVersion);
    const to = optionalCount(result.migrationToVersion);
    const documents = optionalCount(result.migrationDocuments);
    const transformed = optionalCount(result.migrationTransformed);
    const pages = optionalCount(result.migrationPages);
    malformed +=
      Number(from.malformed) +
      Number(to.malformed) +
      Number(documents.malformed) +
      Number(transformed.malformed) +
      Number(pages.malformed);
    migration = {
      manifest,
      fromVersion: from.value,
      toVersion: to.value,
      documents: documents.value,
      transformed: transformed.value,
      pages: pages.value,
    };
  }

  return {
    kind: "recognized",
    state: {
      collection,
      enforcedVersion:
        validation === "enforced" && isPositiveInteger(enforcedRaw) ? enforcedRaw : null,
      ruleCount: result.ruleCount,
      validation: validation as ValidationModeWire,
      migrationKind: migrationKind as MigrationStateKindWire,
      migration,
    },
    malformed,
  };
}

/** The schemas page href, optionally selecting one collection. */
export function schemasHref(collection?: string): string {
  const trimmed = collection?.trim();
  if (trimmed === undefined || trimmed.length === 0) {
    return "/schemas";
  }
  return `/schemas?collection=${encodeURIComponent(trimmed)}`;
}
