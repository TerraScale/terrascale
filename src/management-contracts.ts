import type { CollectionSchema } from "./schema.js";

/** Safe integers remain numbers; larger producer u64 values retain bigint precision. */
export type UInt64Value = number | bigint;
export type Int64Value = number | bigint;

export type SchemaDocument = {
  readonly target: { readonly database: string; readonly branch: string; readonly collection: string };
  readonly identity: { readonly database_id: string; readonly placement: readonly string[] };
  readonly schema: CollectionSchema;
  readonly generation: UInt64Value;
  readonly schema_version: UInt64Value;
  readonly migration?: {
    readonly operation_id: string;
    readonly state: "pending" | "running" | "complete" | "failed";
    readonly steps: readonly { readonly kind: string; readonly from?: string; readonly to?: string; readonly index?: string }[];
    readonly total: UInt64Value;
    readonly completed: UInt64Value;
    readonly next_cursor?: string;
    readonly message: string;
  };
};

export type DatabaseUsageSummary = {
  readonly tenant_id: string;
  readonly database_id: string;
  readonly snapshot_sequence: UInt64Value;
  /** Closed authoritative storage interval boundary; null before the first interval. */
  readonly storage_complete_through_millis: UInt64Value | null;
  readonly meters: readonly { readonly meter: "document_write_units" | "storage_byte_seconds"; readonly units: string }[];
  readonly reads: { readonly total_reads: UInt64Value };
};

export type IndexRecommendations = {
  readonly generation: UInt64Value;
  readonly truncated: boolean;
  readonly recommendations: readonly {
    readonly candidate_id: string;
    readonly collection: string;
    readonly fields: readonly string[];
    readonly score: number;
    readonly maintenance_cost: UInt64Value;
    readonly generation: UInt64Value;
    readonly needs_descriptor: boolean;
  }[];
};

export type CreateWebhookRequest = { readonly endpoint: string };
export type WebhookEndpoint = { readonly id: string; readonly endpoint: string; readonly created_at: UInt64Value; readonly deleted: boolean };
/** Signing secret returned only by creation or rotation; list responses omit it. */
export type WebhookSecretResponse = WebhookEndpoint & { readonly secret: string };
export type RotateWebhookSecretRequest = { readonly overlap_seconds?: number };
export type WebhookList = { readonly webhooks: readonly WebhookEndpoint[] };
export type WebhookDelivery = {
  readonly id: string;
  readonly event_id: string;
  readonly revision: UInt64Value;
  readonly write_index: number;
  readonly retry_started_at: UInt64Value;
  readonly attempts: number;
  readonly next_attempt_at: UInt64Value;
  readonly status: number | null;
  readonly delivered: boolean;
  readonly terminal: boolean;
};
export type WebhookDeliveryLog = { readonly deliveries: readonly WebhookDelivery[] };

export type CreateExportRequest = { readonly idempotency_key: string };
export type ResumeExportRequest = { readonly resume_cursor: string };
export type ExportAccepted = { readonly job_id: string; readonly status_path: string; readonly resume_cursor: string };
export type ExportStatus = {
  readonly job_id: string;
  readonly status: "Accepted" | "Running" | "Cancelled" | "Complete" | "Failed";
  readonly created_at_millis: UInt64Value;
  readonly phase: "Documents" | "History" | "Done";
  readonly cancellation: "available" | "requested" | "complete" | "unavailable";
  readonly pages: UInt64Value;
  readonly records: UInt64Value;
  readonly estimated_records: UInt64Value | null;
  readonly progress_basis_points: number;
  readonly resume_cursor: string;
  readonly manifest: { readonly path: string; readonly length: UInt64Value; readonly checksum: string } | null;
  readonly error: string | null;
};

export type DevelopmentFaultRequest = {
  readonly confirmBranch: string;
  readonly fault: "unavailable" | "latency";
  readonly durationMs: number;
  readonly maxOperations: number;
  readonly delayMs: number;
};
export type DevelopmentFaultResponse = { readonly active: boolean; readonly expiresAtMillis: UInt64Value };
export type DevelopmentLogResponse = {
  readonly entries: readonly { readonly sequence: UInt64Value; readonly action: string; readonly atMillis: UInt64Value }[];
  readonly truncated: boolean;
};

export type ExportList = { readonly exports: readonly ExportStatus[]; readonly next_cursor: string | null };

export type TransferFormat = "json" | "jsonl" | "csv";
export type CreateImportRequest = {
  readonly format: TransferFormat;
  readonly idempotency_key: string;
};
export type ImportJob = {
  readonly committed_records: UInt64Value;
  readonly cursor: UInt64Value;
  readonly database_id: string;
  readonly durability: string;
  readonly error: string | null;
  readonly format: string;
  readonly input_committed: boolean;
  readonly input_digest: string | null;
  readonly input_length: UInt64Value | null;
  readonly operation_id: string;
  readonly pending_cursor: UInt64Value | null;
  readonly pending_operation: string | null;
  readonly pending_records: UInt64Value | null;
  readonly project_id: string;
  readonly state: string;
  readonly tenant_id: string;
};
