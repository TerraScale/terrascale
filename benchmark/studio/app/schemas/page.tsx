import type { Metadata } from "next";

import { CollectionsSchemaTable } from "@/components/schemas/CollectionsSchemaTable";
import { SchemaLiveSeamNote } from "@/components/schemas/SchemaLiveSeamNote";
import { SchemasTypedError } from "@/components/schemas/SchemasTypedError";
import { SchemaStatePanel } from "@/components/schemas/SchemaStatePanel";
import { schemasHref } from "@/components/schemas/vocabulary";
import { createStudioClient } from "@/lib/server/studio-client";

import { loadSchemaCatalog, schemasRouteFailure } from "./data";

// The schemas page (SM-04, TerraBase/TerraBaseV2#28): every collection of the
// catalog with its enforced Schema Version, validation status, and Schema
// Migration state — one typed `query.execute` for the catalog plus one typed
// `schema.status` per collection, bounded. Selecting a collection is a plain
// GET form, so the lookup works without client script; the selected
// collection's panel renders the drive's recorded facts with the engine's own
// state labels. Live updates are the DB-04 seam the panel names; this page
// builds no feed of its own. Typed failures render the designed error route;
// a host-enforced session gate redirects exactly where the host says.
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Schemas — TerraBase Studio",
};

function firstParam(value: string | string[] | undefined): string {
  if (Array.isArray(value)) {
    return value[0] ?? "";
  }
  return value ?? "";
}

export default async function SchemasPage({
  searchParams,
}: {
  readonly searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const selected = firstParam(params.collection).trim();
  const client = createStudioClient();

  let catalog;
  try {
    catalog = await loadSchemaCatalog(client);
  } catch (error) {
    const failure = schemasRouteFailure(error, client);
    return (
      <SchemasTypedError
        code={failure.code}
        message={failure.message}
        retryable={failure.retryable}
        retryHref={schemasHref(selected.length > 0 ? selected : undefined)}
      />
    );
  }

  const selectedRow =
    selected.length > 0 ? catalog.rows.find((row) => row.name === selected) : undefined;

  return (
    <div className="grid gap-4">
      <div>
        <h1 className="text-xl font-semibold text-ink">Schemas</h1>
        <p className="mt-1 max-w-2xl text-sm text-ink-muted">
          Every collection&rsquo;s Document Schema state — the enforced version, the recorded rule
          count, the validation status, and the Schema Migration state — read through the typed
          schema.status operation only. Declaring, validating, and driving migrations are
          backend-operator actions; the CLI owns them, and this page reports the recorded state.
        </p>
      </div>

      <section
        aria-labelledby="schema-select-form-heading"
        className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
        data-testid="schema-select-form"
      >
        <h2 id="schema-select-form-heading" className="text-base font-semibold text-ink">
          Open one collection&rsquo;s schema state
        </h2>
        <form method="get" action="/schemas" className="mt-3 flex flex-wrap items-end gap-3">
          <label className="flex flex-col gap-1 text-sm text-ink-muted">
            <span>Collection</span>
            <input
              type="text"
              name="collection"
              defaultValue={selected}
              aria-label="Collection name"
              placeholder="users"
              className="w-48 rounded-panel border border-line bg-surface-raised px-2 py-1 font-mono text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none"
            />
          </label>
          <button
            type="submit"
            className="rounded-panel border border-line bg-surface-raised px-3 py-1.5 text-sm text-ink transition-colors hover:border-accent hover:text-accent"
          >
            Open schema state
          </button>
        </form>
        {selected.length > 0 && selectedRow === undefined ? (
          <p
            className="mt-2 text-xs text-status-warn"
            role="status"
            data-testid="schema-select-hint"
          >
            <span className="font-mono">{selected}</span> is not in the collections catalog this
            page read{catalog.truncated ? " (the catalog was truncated at the read bound)" : ""}.
          </p>
        ) : null}
      </section>

      <CollectionsSchemaTable
        rows={catalog.rows}
        selected={selected.length > 0 ? selected : null}
        truncated={catalog.truncated}
      />

      <SchemaLiveSeamNote />

      {selectedRow !== undefined && selectedRow.state !== null ? (
        <SchemaStatePanel state={selectedRow.state} malformed={selectedRow.malformed} />
      ) : null}
      {selectedRow !== undefined && selectedRow.state === null ? (
        <p className="text-sm text-status-warn" data-testid="schema-panel-unavailable">
          The schema state of <span className="font-mono">{selectedRow.name}</span> could not be
          read as the contract&rsquo;s shape; the table row carries the typed failure.
        </p>
      ) : null}
    </div>
  );
}
