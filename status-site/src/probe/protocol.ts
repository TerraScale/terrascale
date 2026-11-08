/* What the coordinator asks a vantage point, and what it answers. Vantages
   only observe; the coordinator judges every endpoint's observations. */

/** One URL to probe; endpoints sharing a URL and headers share a target. */
export interface Target {
  key: string;
  url: string;
  /** JSON object of request headers. */
  headers: string;
  /** Return the start of the body, for keyword and JSON checks. */
  body: boolean;
}

export interface ProbeRequest {
  targets: Target[];
}

export interface TargetResult {
  key: string;
  /** The response status, or null when no response arrived. */
  status: number | null;
  latencyMs: number;
  /** Why no response arrived. */
  error: string | null;
  body?: string;
}

export interface ProbeResponse {
  /** Revision of the Worker the vantage runs in. */
  version?: string;
  vantage: string;
  /** The data center the vantage ran in, e.g. "GRU". */
  colo: string | null;
  results: TargetResult[];
}
