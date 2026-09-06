import type { CollectionSchema } from "./schema.js";

/** Safe integers remain numbers; larger producer u64 values retain bigint precision. */
export type UInt64Value = number | bigint;

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
export type WebhookList = { readonly webhooks: readonly WebhookEndpoint[] };
export type WebhookDelivery = {
  readonly id: string;
  readonly event_id: string;
  readonly revision: UInt64Value;
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
  readonly status: "Accepted" | "Running" | "Complete" | "Failed";
  readonly records: UInt64Value;
  readonly estimated_records: UInt64Value | null;
  readonly progress_basis_points: number;
  readonly resume_cursor: string;
  readonly manifest: { readonly path: string; readonly length: UInt64Value; readonly checksum: string } | null;
  readonly error: string | null;
};
