// Typed error state for the documents routes (DB-03): rendered server-side
// from the typed API error (code, retryability) so the operator sees the
// host's error identity — collapsing to a digest at the boundary would lose
// it. app/documents/error.tsx remains the last-resort boundary for unexpected
// failures; this is the designed, tested error route, mirroring the DB-02
// EngineStatusError convention for the list/detail pages.

import { EffectButton } from "@/components/ui/EffectButton";

export type DocumentsErrorScope = "collections" | "documents" | "document";

const TITLES: Record<DocumentsErrorScope, string> = {
  collections: "Collections unavailable",
  documents: "Documents unavailable",
  document: "Document unavailable",
};

export interface DocumentsTypedErrorProps {
  readonly scope: DocumentsErrorScope;
  readonly code: string;
  readonly message: string;
  readonly retryable: boolean;
  /** Route to re-navigate to for the Try again action (re-runs the typed read). */
  readonly retryHref: string;
}

export function DocumentsTypedError({
  scope,
  code,
  message,
  retryable,
  retryHref,
}: DocumentsTypedErrorProps) {
  return (
    <main className="flex min-h-64 items-center justify-center px-5 py-8">
      <section
        aria-labelledby="documents-error-title"
        className="grid w-full max-w-md gap-4 rounded-panel border border-line bg-surface p-6 shadow-panel"
        data-testid="documents-error"
      >
        <div className="grid gap-2">
          <h1 id="documents-error-title" className="text-xl font-semibold text-ink">
            {TITLES[scope]}
          </h1>
          <p className="text-sm text-ink-muted">{message}</p>
          <dl className="grid gap-1 font-mono text-xs text-ink-muted">
            <div className="flex gap-2">
              <dt>code</dt>
              <dd className="text-ink" data-testid="documents-error-code">
                {code}
              </dd>
            </div>
            <div className="flex gap-2">
              <dt>retryable</dt>
              <dd className="text-ink" data-testid="documents-error-retryable">
                {String(retryable)}
              </dd>
            </div>
          </dl>
        </div>
        <div className="flex flex-wrap gap-2">
          <EffectButton asLink href={retryHref}>
            Try again
          </EffectButton>
          <EffectButton asLink href="/documents">
            All collections
          </EffectButton>
        </div>
      </section>
    </main>
  );
}
