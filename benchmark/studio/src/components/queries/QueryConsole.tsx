"use client";

// The query console island (DB-05).
//
// Everything the console does crosses the public seam as a typed operation
// through a server action: running a query is one `query.execute`, saving a
// query is one idempotent `document.write`. The browser never talks to the API
// host directly. Execution is read-only by default; the only mutating action
// sits behind an explicit two-step confirmation.
//
// The streaming execution indicator is a live region driven by the action's
// pending state and a wall-clock ticker: while the typed operation is in
// flight the console stays interactive and the previous results stay visible.

import { useActionState, useEffect, useState } from "react";

import { OperationErrorPanel } from "@/components/queries/OperationErrorPanel";
import { QueryMetadata } from "@/components/queries/QueryMetadata";
import { QueryResults } from "@/components/queries/QueryResults";
import {
  DEFAULT_PAGE_SIZE,
  PAGE_SIZES,
  pagerDescriptor,
  slicePage,
  type ConsoleOutcome,
  type MutationOutcome,
} from "@/components/queries/query-console-model";
import { formatInteger } from "@/lib/format";
// Server actions live with the route (app/queries/actions.ts); the `@/` alias
// maps to src/, so the route tree is reached relatively.
import { runQueryAction, saveQueryAction } from "../../../app/queries/actions";

export function QueryConsole() {
  const [outcome, runAction, isRunning] = useActionState<ConsoleOutcome, FormData>(runQueryAction, {
    kind: "idle",
  });
  const [pageSize, setPageSize] = useState<number>(DEFAULT_PAGE_SIZE);
  const [page, setPage] = useState<number>(1);
  const [elapsedMs, setElapsedMs] = useState<number>(0);

  useEffect(() => {
    if (!isRunning) {
      return;
    }
    const startedAt = Date.now();
    setElapsedMs(0);
    const timer = setInterval(() => {
      setElapsedMs(Date.now() - startedAt);
    }, 200);
    return () => clearInterval(timer);
  }, [isRunning]);

  const rows = outcome.kind === "results" ? outcome.rows : [];
  const pager = pagerDescriptor(rows.length, pageSize, page);
  const pageRows = slicePage(rows, pageSize, page);

  return (
    <section
      aria-labelledby="query-console-heading"
      className="grid gap-4"
      data-testid="query-console"
    >
      <form
        action={runAction}
        className="grid gap-3 rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
      >
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <h2 id="query-console-heading" className="text-base font-semibold text-ink">
            Query console
          </h2>
          <p className="text-xs text-ink-faint">
            One typed <code>query.execute</code> operation per run, server-side.
          </p>
        </div>
        <label className="grid gap-1 text-sm text-ink-muted" htmlFor="query-console-text">
          Query text (V1-compatible grammar)
        </label>
        <textarea
          id="query-console-text"
          name="query"
          rows={3}
          spellCheck={false}
          defaultValue="from collection"
          className="rounded-panel border border-line bg-surface-raised px-3 py-2 font-mono text-sm text-ink"
          data-testid="query-console-text"
        />
        <div className="flex flex-wrap items-center gap-2">
          <button
            type="submit"
            onClick={() => {
              setPage(1);
            }}
            className="rounded-panel border border-line bg-surface-raised px-3 py-1.5 text-sm text-ink transition-colors hover:border-accent hover:text-accent"
            data-testid="query-console-run"
          >
            Run query
          </button>
          <span aria-live="polite" role="status" className="text-xs text-ink-muted">
            {isRunning ? (
              <span className="flex items-center gap-1.5" data-testid="query-running">
                <span aria-hidden="true" className="text-status-warn">
                  ◌
                </span>
                Executing the typed query operation… {(elapsedMs / 1000).toFixed(1)}s
              </span>
            ) : null}
          </span>
        </div>
        <p className="text-xs text-ink-faint">
          Read-only by default. Saving a query is the only mutating action here, and it asks for
          confirmation first.
        </p>
      </form>

      {outcome.kind === "error" ? (
        <OperationErrorPanel
          outcome={outcome}
          testId="query-error"
          title="Query operation failed"
        />
      ) : null}

      {outcome.kind === "results" ? (
        <>
          <QueryResults rows={pageRows} pager={pager} />
          <Pager
            pager={pager}
            pageSize={pageSize}
            onSelectPageSize={(size) => {
              setPageSize(size);
              setPage(1);
            }}
            onPrevious={() => setPage(pager.page - 1)}
            onNext={() => setPage(pager.page + 1)}
          />
          <QueryMetadata outcome={outcome} />
          {/* Keyed by the execution identity so every new query starts from a
              clean save state: no receipt, no half-open confirmation. */}
          <SaveQuery key={outcome.executedAt} queryText={outcome.queryText} />
        </>
      ) : null}
    </section>
  );
}

function Pager({
  pager,
  pageSize,
  onSelectPageSize,
  onPrevious,
  onNext,
}: {
  pager: ReturnType<typeof pagerDescriptor>;
  pageSize: number;
  onSelectPageSize: (size: number) => void;
  onPrevious: () => void;
  onNext: () => void;
}) {
  return (
    <nav
      aria-label="Result pages"
      className="flex flex-wrap items-center gap-3 rounded-panel border border-line bg-surface px-4 py-2 text-sm shadow-panel"
      data-testid="query-pager"
    >
      <button
        type="button"
        onClick={onPrevious}
        disabled={!pager.hasPrev}
        className="rounded-panel border border-line bg-surface-raised px-3 py-1 text-sm text-ink transition-colors hover:border-accent hover:text-accent disabled:cursor-default disabled:text-ink-faint disabled:hover:border-line disabled:hover:text-ink-faint"
        data-testid="query-pager-previous"
      >
        Previous
      </button>
      <p className="font-mono text-xs text-ink" data-testid="query-pager-position">
        page {formatInteger(pager.page)} of {formatInteger(pager.pageCount)}
      </p>
      <button
        type="button"
        onClick={onNext}
        disabled={!pager.hasNext}
        className="rounded-panel border border-line bg-surface-raised px-3 py-1 text-sm text-ink transition-colors hover:border-accent hover:text-accent disabled:cursor-default disabled:text-ink-faint disabled:hover:border-line disabled:hover:text-ink-faint"
        data-testid="query-pager-next"
      >
        Next
      </button>
      <span className="ml-auto flex items-center gap-1 text-xs text-ink-muted">
        <span className="sr-only">Rows per page</span>
        {PAGE_SIZES.map((size) => (
          <button
            key={size}
            type="button"
            onClick={() => onSelectPageSize(size)}
            aria-pressed={size === pageSize}
            className={
              size === pageSize
                ? "rounded-panel border border-accent px-2 py-0.5 font-mono text-xs text-accent"
                : "rounded-panel border border-line px-2 py-0.5 font-mono text-xs text-ink-muted transition-colors hover:border-accent hover:text-accent"
            }
            data-testid="query-pager-size"
            data-page-size={size}
          >
            {size}
          </button>
        ))}
      </span>
    </nav>
  );
}

function SaveQuery({ queryText }: { queryText: string }) {
  const [mutation, saveAction, isSaving] = useActionState<MutationOutcome, FormData>(
    saveQueryAction,
    { kind: "idle" },
  );
  const [confirming, setConfirming] = useState<boolean>(false);
  return (
    <section
      aria-labelledby="save-query-heading"
      className="grid gap-2 rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
      data-testid="save-query"
    >
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 id="save-query-heading" className="text-base font-semibold text-ink">
          Save query
        </h2>
        <p className="text-xs text-ink-faint">
          A typed, idempotent <code>document.write</code> into the <code>studio-saved-queries</code>{" "}
          collection. Nothing is written until you confirm.
        </p>
      </div>

      {confirming ? (
        <form action={saveAction} className="grid gap-2" data-testid="save-query-confirm">
          <input type="hidden" name="query" value={queryText} />
          <p className="text-sm text-ink-muted">Confirm saving this query as a document:</p>
          <pre className="max-h-24 overflow-auto rounded bg-surface-raised px-2 py-1 font-mono text-xs text-ink">
            {queryText}
          </pre>
          <div className="flex flex-wrap gap-2">
            <button
              type="submit"
              className="rounded-panel border border-status-warn bg-surface-raised px-3 py-1.5 text-sm text-ink transition-colors hover:border-accent hover:text-accent"
              data-testid="save-query-confirm-submit"
            >
              Confirm save
            </button>
            <button
              type="button"
              onClick={() => setConfirming(false)}
              className="rounded-panel border border-line bg-surface-raised px-3 py-1.5 text-sm text-ink transition-colors hover:border-accent hover:text-accent"
              data-testid="save-query-cancel"
            >
              Cancel
            </button>
          </div>
          <p aria-live="polite" role="status" className="text-xs text-ink-muted">
            {isSaving ? (
              <span data-testid="save-query-running">
                Writing the typed document.write operation…
              </span>
            ) : null}
          </p>
        </form>
      ) : (
        <button
          type="button"
          onClick={() => setConfirming(true)}
          className="justify-self-start rounded-panel border border-line bg-surface-raised px-3 py-1.5 text-sm text-ink transition-colors hover:border-accent hover:text-accent"
          data-testid="save-query-request"
        >
          Save query
        </button>
      )}

      {mutation.kind === "receipt" ? (
        <dl
          className="grid gap-1 rounded bg-surface-raised px-3 py-2 font-mono text-xs text-ink"
          data-testid="save-query-receipt"
        >
          <div className="flex gap-2">
            <dt className="text-ink-muted">operationId</dt>
            <dd data-testid="save-query-receipt-operation-id">{mutation.receipt.operationId}</dd>
          </div>
          <div className="flex gap-2">
            <dt className="text-ink-muted">document</dt>
            <dd>
              <span data-testid="save-query-receipt-collection">{mutation.receipt.collection}</span>
              {" / "}
              <span data-testid="save-query-receipt-document-id">
                {mutation.receipt.documentId}
              </span>
            </dd>
          </div>
          <div className="flex gap-2">
            <dt className="text-ink-muted">version</dt>
            <dd data-testid="save-query-receipt-version">
              {formatInteger(mutation.receipt.version)}
            </dd>
          </div>
        </dl>
      ) : null}

      {mutation.kind === "error" ? (
        <OperationErrorPanel
          outcome={mutation}
          testId="save-query-error"
          title="Save query failed"
        />
      ) : null}
    </section>
  );
}
