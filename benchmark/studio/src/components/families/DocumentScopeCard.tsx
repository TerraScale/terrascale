// The document scope identity card and the designed missing state for the
// per-document family panels route (DB-06): the identity arrives through the
// typed `document.read` — the family panels scope one real document, so a
// missing document is the typed read's designed `null` outcome and renders
// its own route, not an error.

import { documentHref } from "@/components/documents/vocabulary";
import { EffectButton } from "@/components/ui/EffectButton";
import { formatInteger } from "@/lib/format";
import { familiesHref } from "@/components/families/vocabulary";

export interface DocumentScopeCardProps {
  readonly collection: string;
  readonly documentId: string;
  readonly version: number;
}

export function DocumentScopeCard({ collection, documentId, version }: DocumentScopeCardProps) {
  return (
    <section
      aria-labelledby="document-scope-heading"
      className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
      data-testid="document-scope"
    >
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 id="document-scope-heading" className="text-base font-semibold text-ink">
          Document scope
        </h2>
        <div className="flex flex-wrap gap-2">
          <EffectButton asLink href={documentHref(collection, documentId)}>
            Open in documents
          </EffectButton>
          <EffectButton asLink href="/families">
            All Data Family kinds
          </EffectButton>
        </div>
      </div>
      <dl
        className="mt-2 grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2"
        data-testid="document-scope-metadata"
      >
        <div className="flex gap-2">
          <dt className="text-ink-muted">Collection</dt>
          <dd className="font-mono text-ink" data-testid="document-collection">
            {collection}
          </dd>
        </div>
        <div className="flex gap-2">
          <dt className="text-ink-muted">Document id</dt>
          <dd className="font-mono text-ink" data-testid="document-id">
            {documentId}
          </dd>
        </div>
        <div className="flex gap-2">
          <dt className="text-ink-muted">Sequence</dt>
          <dd className="font-mono text-ink tabular-nums" data-testid="document-sequence">
            {formatInteger(version)}
          </dd>
        </div>
      </dl>
      <p className="mt-2 text-xs text-ink-faint" data-testid="document-scope-note">
        The panels below read this document&rsquo;s family state through the typed family.read
        operation; the sequence is the document&rsquo;s published version from the typed
        document.read.
      </p>
    </section>
  );
}

export interface DocumentScopeMissingProps {
  readonly collection: string;
  readonly documentId: string;
}

/** The designed missing state: the typed document read answered null. */
export function DocumentScopeMissing({ collection, documentId }: DocumentScopeMissingProps) {
  return (
    <main className="flex min-h-64 items-center justify-center px-5 py-8">
      <section
        aria-labelledby="document-scope-missing-title"
        className="grid w-full max-w-md gap-4 rounded-panel border border-line bg-surface p-6 shadow-panel"
        data-testid="document-scope-missing"
      >
        <div className="grid gap-2">
          <h1 id="document-scope-missing-title" className="text-xl font-semibold text-ink">
            Document not found
          </h1>
          <p className="text-sm text-ink-muted">
            The typed document.read answered null for this scope, so there is no document whose
            family state the panels could read.
          </p>
          <p className="font-mono text-xs text-ink-muted">
            {collection}/{documentId}
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <EffectButton asLink href={familiesHref(collection, documentId)}>
            Try again
          </EffectButton>
          <EffectButton asLink href="/documents">
            Browse collections
          </EffectButton>
        </div>
      </section>
    </main>
  );
}
