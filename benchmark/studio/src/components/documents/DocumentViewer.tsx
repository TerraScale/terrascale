// Document viewer (DB-03): a server component fed exclusively by the typed
// `document.read` result — never raw fetch, never engine internals. Metadata
// renders the typed surface's own identity and sequence vocabulary; the value
// renders as pretty JSON truncated at an explicit character limit, so a large
// document ships and renders only its bounded prefix with the honest full
// size shown.

import type { DocumentRecord } from "@/api/generated/client";
import { EffectButton } from "@/components/ui/EffectButton";
import { formatInteger } from "@/lib/format";
import {
  PRETTY_JSON_CHAR_LIMIT,
  collectionHref,
  prettyJsonTruncated,
} from "@/components/documents/vocabulary";

export interface DocumentViewerProps {
  readonly document: DocumentRecord;
}

export function DocumentViewer({ document }: DocumentViewerProps) {
  const pretty = prettyJsonTruncated(document.value);

  return (
    <section aria-labelledby="document-viewer-heading" className="grid gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 id="document-viewer-heading" className="text-base font-semibold text-ink">
          Document
        </h2>
        <EffectButton asLink href={collectionHref(document.collection)}>
          Back to {document.collection}
        </EffectButton>
      </div>

      <div className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
        <h3 className="text-sm font-semibold text-ink">Metadata</h3>
        <dl
          className="mt-2 grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2"
          data-testid="document-metadata"
        >
          <div className="flex gap-2">
            <dt className="text-ink-muted">Collection</dt>
            <dd className="font-mono text-ink" data-testid="document-collection">
              {document.collection}
            </dd>
          </div>
          <div className="flex gap-2">
            <dt className="text-ink-muted">Document id</dt>
            <dd className="font-mono text-ink" data-testid="document-id">
              {document.id}
            </dd>
          </div>
          <div className="flex gap-2">
            <dt className="text-ink-muted">Sequence</dt>
            <dd className="font-mono text-ink tabular-nums" data-testid="document-sequence">
              {formatInteger(document.version)}
            </dd>
          </div>
          <div className="flex gap-2">
            <dt className="text-ink-muted">Pretty size</dt>
            <dd className="font-mono text-ink tabular-nums" data-testid="document-size">
              {formatInteger(pretty.totalChars)} characters
            </dd>
          </div>
        </dl>
        <p className="mt-2 text-xs text-ink-faint">
          The sequence is the document&apos;s published version (DocumentMetadata through the typed
          read). Per-write operation identity (OperationId) rides typed write receipts and is not
          part of this read.
        </p>
      </div>

      <div className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel">
        <h3 className="text-sm font-semibold text-ink">Value (pretty JSON)</h3>
        {pretty.truncated ? (
          <p
            className="mt-2 font-mono text-xs text-status-warn"
            data-testid="document-truncated"
            role="status"
          >
            Truncated: showing {formatInteger(PRETTY_JSON_CHAR_LIMIT)} of{" "}
            {formatInteger(pretty.totalChars)} characters
          </p>
        ) : null}
        <pre
          className="mt-2 overflow-x-auto rounded bg-surface-raised p-3 font-mono text-xs leading-relaxed text-ink"
          data-testid="document-json"
        >
          {pretty.text}
        </pre>
      </div>
    </section>
  );
}
