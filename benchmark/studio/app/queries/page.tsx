import type { Metadata } from "next";

import { QueryConsole } from "@/components/queries/QueryConsole";

// Queries page (DB-05): the query console. Running a query is one typed
// `query.execute` operation dispatched through a server action, so the browser
// never talks to the API host directly; the console is read-only by default and
// its single mutating action (saving the query) is a typed `document.write`
// behind an explicit confirmation.
//
// The route renders without a server-side read on purpose: the typed operation
// runs when the operator runs it, so the console is immediately interactive and
// a long-running query streams its execution indicator without blocking the UI.
// The host-enforced operator session is honored per operation inside the
// action (a session.unauthenticated answer redirects to the host's loginUrl).
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Queries — TerraBase Studio",
};

export default function QueriesPage() {
  return (
    <div className="grid gap-4">
      <div>
        <h1 className="text-xl font-semibold text-ink">Queries</h1>
        <p className="mt-1 max-w-2xl text-sm text-ink-muted">
          Run a typed query operation, page the rows it returned, and read the query-level metadata
          the host reported — matched count and per-index staleness observations, verbatim.
        </p>
      </div>
      <QueryConsole />
    </div>
  );
}
