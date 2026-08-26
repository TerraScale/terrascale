"use server";

// Server actions for the queries views (DB-05).
//
// Every action is exactly one typed operation against the API host through the
// generated client — running a query is one `query.execute`, saving a query is
// one idempotent `document.write`. The browser never talks to the host
// directly, and no action invents engine behavior: the studio's own
// pre-dispatch validation (an empty query text) is labeled as the studio's,
// never presented as the engine's error vocabulary.
//
// Typed failures come back as typed outcomes so the console renders the
// host's error identity verbatim. The host-enforced operator session is
// honored exactly as on the home route: a `session.unauthenticated` answer
// redirects to the host's loginUrl, and the studio adds no auth logic of its
// own (charter PD-2026-08-26-11).

import { redirect } from "next/navigation";

import { TerrabaseApiError, type TerrabaseStudioClient } from "@/api/generated/client";
import type {
  ConsoleOutcome,
  ErrorOutcome,
  MutationOutcome,
} from "@/components/queries/query-console-model";
import {
  errorOutcome,
  executionOutcomeFromResult,
  normalizeQueryText,
  SAVED_QUERIES_COLLECTION,
} from "@/components/queries/query-console-model";
import { createStudioClient } from "@/lib/server/studio-client";
import { resolveLoginUrl } from "@/lib/server/session";

/**
 * Runs one typed query operation.
 *
 * The console's own validation rejects empty text before any dispatch; every
 * other failure is the host's typed error, carried through unmodified.
 */
export async function runQueryAction(
  _previous: ConsoleOutcome,
  formData: FormData,
): Promise<ConsoleOutcome> {
  const raw = formData.get("query");
  const normalized = normalizeQueryText(typeof raw === "string" ? raw : "");
  if (!normalized.ok) {
    return {
      kind: "error",
      code: "studio.query-empty",
      message:
        "The console did not dispatch an empty query. The host's own typed validation answers " +
        "with the engine's error vocabulary; this one never left the studio.",
      retryable: false,
    };
  }

  const client = createStudioClient();
  const startedAt = Date.now();
  try {
    const result = await client.queryExecute({ input: { kind: "v1", text: normalized.text } });
    return executionOutcomeFromResult(result, {
      queryText: normalized.text,
      elapsedMs: Date.now() - startedAt,
      executedAt: new Date().toISOString(),
    });
  } catch (error) {
    return typedFailure(error, client);
  }
}

/**
 * Saves the current query text as a document through one idempotent typed
 * write. The document identity and the operation identity are derived from the
 * query text, so saving the same query again replays the same receipt instead
 * of writing a second document.
 */
export async function saveQueryAction(
  _previous: MutationOutcome,
  formData: FormData,
): Promise<MutationOutcome> {
  const raw = formData.get("query");
  const normalized = normalizeQueryText(typeof raw === "string" ? raw : "");
  if (!normalized.ok) {
    return {
      kind: "error",
      code: "studio.query-empty",
      message: "The console did not save an empty query.",
      retryable: false,
    };
  }

  const client = createStudioClient();
  const identity = await savedQueryIdentity(normalized.text);
  try {
    const receipt = await client.documentWrite(
      {
        document: {
          id: identity.documentId,
          collection: SAVED_QUERIES_COLLECTION,
          value: {
            kind: "studio-saved-query",
            text: normalized.text,
            savedAt: new Date().toISOString(),
          },
        },
        operationId: identity.operationId,
      },
      { idempotencyKey: identity.operationId },
    );
    return { kind: "receipt", receipt };
  } catch (error) {
    return typedFailure(error, client);
  }
}

/** Derives the stable document and operation identity of one saved query. */
async function savedQueryIdentity(queryText: string): Promise<{
  documentId: string;
  operationId: string;
}> {
  const digest = await sha256Short(queryText);
  return {
    documentId: `query-${digest}`,
    operationId: `studio-save-query-${digest}`,
  };
}

async function sha256Short(value: string): Promise<string> {
  const bytes = new TextEncoder().encode(value);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0"))
    .slice(0, 12)
    .join("");
}

/**
 * Converts a typed API error into a typed outcome, honoring the host-enforced
 * operator session by redirecting to the host's loginUrl. Anything that is not
 * a typed API error propagates to the route's error boundary, exactly as the
 * home route treats unexpected failures.
 */
function typedFailure(error: unknown, client: TerrabaseStudioClient): ErrorOutcome {
  if (!(error instanceof TerrabaseApiError)) {
    throw error;
  }
  if (
    error.code === "session.unauthenticated" &&
    error.loginUrl !== null &&
    error.loginUrl.length > 0
  ) {
    redirect(resolveLoginUrl(client.baseUrl, error.loginUrl));
  }
  return errorOutcome({ code: error.code, message: error.message, retryable: error.retryable });
}
