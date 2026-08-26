import type { Metadata } from "next";
import { redirect } from "next/navigation";

import { FamilyKindList } from "@/components/families/FamilyKindList";
import { familiesHref } from "@/components/families/vocabulary";

// Data families index (DB-06): the closed FamilyKind inventory in CONTEXT.md's
// exact vocabulary — the three kinds this page views through typed family
// reads, and the remaining kinds explicitly marked not yet viewable (never
// silently missing, never a dead link). The document-scope form below opens
// one document's family panels; it is a plain GET form, so the lookup works
// without client script. A lookup with both identities redirects to the
// panels route; the panels themselves do every typed read.
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Families — TerraBase Studio",
};

function firstParam(value: string | string[] | undefined): string {
  if (Array.isArray(value)) {
    return value[0] ?? "";
  }
  return value ?? "";
}

export default async function FamiliesPage({
  searchParams,
}: {
  readonly searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const collection = firstParam(params.collection).trim();
  const documentId = firstParam(params.documentId).trim();
  if (collection.length > 0 && documentId.length > 0) {
    redirect(familiesHref(collection, documentId));
  }

  return (
    <div className="grid gap-4">
      <div>
        <h1 className="text-xl font-semibold text-ink">Families</h1>
        <p className="mt-1 max-w-2xl text-sm text-ink-muted">
          Family-aware views over one document&rsquo;s Data Family state — counters, time-series,
          and attachments — read through typed family operations only. Every kind of the
          engine&rsquo;s closed set appears below: the three this page views, and the rest
          explicitly marked not yet viewable.
        </p>
      </div>

      <section
        aria-labelledby="family-scope-form-heading"
        className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
        data-testid="family-scope-form"
      >
        <h2 id="family-scope-form-heading" className="text-base font-semibold text-ink">
          Open a document&rsquo;s family panels
        </h2>
        <p className="mt-1 text-xs text-ink-faint">
          The panels scope one real document; the route loads its identity through the typed
          document.read before any family read.
        </p>
        <form method="get" action="/families" className="mt-3 flex flex-wrap items-end gap-3">
          <label className="flex flex-col gap-1 text-sm text-ink-muted">
            <span>Collection</span>
            <input
              type="text"
              name="collection"
              defaultValue={collection}
              required
              aria-label="Collection name"
              placeholder="families-demo"
              className="w-48 rounded-panel border border-line bg-surface-raised px-2 py-1 font-mono text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none"
            />
          </label>
          <label className="flex flex-col gap-1 text-sm text-ink-muted">
            <span>Document id</span>
            <input
              type="text"
              name="documentId"
              defaultValue={documentId}
              required
              aria-label="Document id"
              placeholder="sensors-001"
              className="w-48 rounded-panel border border-line bg-surface-raised px-2 py-1 font-mono text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none"
            />
          </label>
          <button
            type="submit"
            className="rounded-panel border border-line bg-surface-raised px-3 py-1.5 text-sm text-ink transition-colors hover:border-accent hover:text-accent"
          >
            Open family panels
          </button>
        </form>
        {collection.length > 0 !== documentId.length > 0 ? (
          <p
            className="mt-2 text-xs text-status-warn"
            role="status"
            data-testid="family-scope-hint"
          >
            Both a collection and a document id are needed to open the panels.
          </p>
        ) : null}
      </section>

      <FamilyKindList variant="full" />
    </div>
  );
}
