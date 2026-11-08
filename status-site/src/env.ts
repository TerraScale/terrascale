/* The Worker's bindings. */

import type { Coordinator } from "./probe/coordinator";
import type { VantagePoint } from "./vantage/point";

/** The Email Sending binding's send method; the message fields we use. */
export interface EmailSender {
  send(message: {
    from: { email: string; name: string };
    to: string;
    subject: string;
    text: string;
    html: string;
    headers: Record<string, string>;
  }): Promise<{ messageId: string }>;
}

export interface Env {
  /** Full source revision set by the main deployment job. */
  VERSION?: string;
  STATUS_DB: D1Database;
  COORDINATOR: DurableObjectNamespace<Coordinator>;
  /** Existing Europe/Asia Pacific objects (src/probe/vantages.ts). */
  VANTAGE: DurableObjectNamespace<VantagePoint>;
  /** Private fetch handlers with explicit regional placement. */
  VANTAGE_SFO: Fetcher;
  VANTAGE_GRU: Fetcher;
  /** Encrypts admin cookies and signs CSRF tokens (a Worker secret). */
  ADMIN_SESSION_SECRET?: string;
  /** Limits admin sign-in attempts per client. */
  ADMIN_LOGIN_LIMITER: RateLimit;
  /** The public origin, for links in notifications. */
  PUBLIC_ORIGIN?: string;
  /** Signs subscription links and generic webhooks (a Worker secret). */
  SUBSCRIPTION_SECRET?: string;
  /** Cloudflare Email Sending, from the send_email binding in wrangler.jsonc. */
  EMAIL: EmailSender;
  /** The data API canary's documents and scoped keys, as JSON keyed by
      canary endpoint id (a Worker secret; see src/probe/canary.ts). */
  DATA_API_CANARY?: string;
  /** TerraBase health and document API probes' origins and keys, as JSON
      keyed by endpoint id (a Worker secret; see src/probe/terrabase.ts). */
  TERRABASE_PROBES?: string;
  /** Limits subscription requests per client. */
  SUBSCRIBE_LIMITER?: RateLimit;
}
