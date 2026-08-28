// The DB-04 live-update seam, rendered honestly (SM-04): the schemas page is
// a typed read per navigation, and the subscription surface that will carry
// schema-state deltas belongs to a sibling ticket. This note names that seam
// instead of shipping a second feed — when DB-04 lands, the panel swaps this
// note for its subscription without touching the vocabulary above it.

import { SCHEMA_LIVE_SEAM, SCHEMA_LIVE_SEAM_LABEL } from "@/components/schemas/vocabulary";

export function SchemaLiveSeamNote() {
  return (
    <p
      className="mt-3 text-xs text-ink-faint"
      data-testid="schema-live-seam"
      data-seam-owner={SCHEMA_LIVE_SEAM.owner}
      data-seam-landed={String(SCHEMA_LIVE_SEAM.landed)}
    >
      <span className="font-medium text-ink-muted">{SCHEMA_LIVE_SEAM_LABEL}.</span>{" "}
      {SCHEMA_LIVE_SEAM.note}
    </p>
  );
}
