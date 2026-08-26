// The closed FamilyKind inventory (DB-06): every kind from the engine's ten,
// rendered explicitly — the three this page views through typed family reads,
// and the remaining kinds marked not yet viewable (muted, non-interactive, no
// dead links) so nothing is silently missing. The kind names are the engine's
// reserved family record keys verbatim (src/families/state.rs family_key,
// guarded by tests/families-page.test.mjs).

import {
  ATTACHMENT_STORE_SEMANTICS,
  COUNTER_DELETE_SEMANTICS,
  COUNTER_INCREMENT_SEMANTICS,
  NOT_YET_VIEWABLE_FAMILY_KINDS,
  NOT_YET_VIEWABLE_LABEL,
  TIME_SERIES_RANGE_SEMANTICS,
  VIEWABLE_FAMILY_KINDS,
  type FamilyKindWire,
} from "@/components/families/vocabulary";

const VIEWABLE_NOTES: Record<string, string> = {
  attachments: ATTACHMENT_STORE_SEMANTICS,
  counters: `${COUNTER_INCREMENT_SEMANTICS} ${COUNTER_DELETE_SEMANTICS}`,
  "time-series": TIME_SERIES_RANGE_SEMANTICS,
};

function KindChip({ kind }: { readonly kind: FamilyKindWire }) {
  return (
    <span className="rounded-panel border border-line bg-surface-raised px-2 py-0.5 font-mono text-xs text-ink">
      {kind}
    </span>
  );
}

export interface FamilyKindListProps {
  /**
   * `full` renders both groups with their semantics notes (the families
   * index); `compact` renders only the not-yet-viewable kinds (the per-document
   * panels route, where the viewable three are the panels themselves).
   */
  readonly variant: "full" | "compact";
}

export function FamilyKindList({ variant }: FamilyKindListProps) {
  return (
    <section
      aria-labelledby="family-kind-list-heading"
      className="rounded-panel border border-line bg-surface px-4 py-3 shadow-panel"
      data-testid="family-kind-list"
    >
      <h2 id="family-kind-list-heading" className="text-base font-semibold text-ink">
        Data Family kinds
      </h2>
      <p className="mt-1 max-w-2xl text-xs text-ink-faint">
        A Data Family is a V1-named group of related data capabilities whose parity obligations are
        tracked together while each public contract remains explicit (CONTEXT.md).
      </p>
      {variant === "full" ? (
        <div className="mt-3 grid gap-2" data-testid="family-kind-viewable">
          <h3 className="text-sm font-semibold text-ink">Viewable through typed family reads</h3>
          <ul className="grid gap-2">
            {VIEWABLE_FAMILY_KINDS.map((kind) => (
              <li key={kind} className="flex flex-wrap items-baseline gap-2" data-kind={kind}>
                <KindChip kind={kind} />
                <span className="text-xs text-ink-muted">{VIEWABLE_NOTES[kind]}</span>
              </li>
            ))}
          </ul>
        </div>
      ) : null}
      <div className="mt-3 grid gap-2" data-testid="family-kind-not-yet-viewable">
        <h3 className="text-sm font-semibold text-ink">{NOT_YET_VIEWABLE_LABEL}</h3>
        <ul className="flex flex-wrap gap-2">
          {NOT_YET_VIEWABLE_FAMILY_KINDS.map((kind) => (
            <li key={kind}>
              <span
                aria-disabled="true"
                title={`${kind} is not yet viewable in the studio`}
                className="inline-flex cursor-default items-baseline gap-1 rounded-panel border border-line bg-surface px-2 py-0.5 text-xs text-ink-faint"
                data-testid={`family-kind-${kind}`}
              >
                <span className="font-mono">{kind}</span>
                <span aria-hidden="true">·</span>
                <span>{NOT_YET_VIEWABLE_LABEL}</span>
                <span className="sr-only"> (no view exists yet)</span>
              </span>
            </li>
          ))}
        </ul>
        <p className="text-xs text-ink-faint">
          These kinds stay in the engine&rsquo;s closed set; the studio adds no view for them until
          one lands with its own typed operations — never a silent omission and never a dead link.
        </p>
      </div>
    </section>
  );
}
