// Not-found state for the document viewer (DB-03): the typed `document.read`
// answers `document: null` when the document does not exist, and that
// designed outcome renders as its own honest route — not as an error.

import { EffectButton } from "@/components/ui/EffectButton";
import { collectionHref } from "@/components/documents/vocabulary";

export interface DocumentMissingProps {
  readonly collection: string;
  readonly documentId: string;
}

export function DocumentMissing({ collection, documentId }: DocumentMissingProps) {
  return (
    <main className="flex min-h-64 items-center justify-center px-5 py-8">
      <section
        aria-labelledby="document-missing-title"
        className="grid w-full max-w-md gap-4 rounded-panel border border-line bg-surface p-6 shadow-panel"
        data-testid="document-missing"
      >
        <div className="grid gap-2">
          <h1 id="document-missing-title" className="text-xl font-semibold text-ink">
            Document not found
          </h1>
          <p className="text-sm text-ink-muted">
            The typed document read returned no document for{" "}
            <span className="font-mono text-ink">{documentId}</span> in collection{" "}
            <span className="font-mono text-ink">{collection}</span>.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <EffectButton asLink href={collectionHref(collection)}>
            Back to {collection}
          </EffectButton>
        </div>
      </section>
    </main>
  );
}
