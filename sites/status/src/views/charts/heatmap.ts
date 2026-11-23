/* A calendar heatmap: one cell per UTC day in week columns, colored by the
   day's state. Like a bar strip it is one focusable image whose cells the
   arrow keys walk, each opening a hover card. */

import { STATE_LABELS, stateAttr, type Bucket } from "../../domain/status";
import { bucketStarts } from "../../domain/range";
import { DAY, formatDay, startOfDay } from "../../format";
import { html, type Html } from "../../html";
import type { CardContent } from "../components";

const CELL = 11;
const GAP = 3;
const LEFT = 28;
const TOP = 16;

const month = new Intl.DateTimeFormat("en", { month: "short", timeZone: "UTC" });

/** Draws `days`, UTC day buckets in order; each lands in its date's slot, so
    days left out simply leave gaps. */
export function heatmap(days: readonly Bucket[], label: string, card: (day: Bucket) => CardContent): Html {
  const first = days[0];
  if (!first) return html``;
  // Columns are weeks starting on Sunday.
  const offset = new Date(first.start).getUTCDay();
  const slotOf = (day: Bucket) => offset + Math.round((day.start - first.start) / DAY);
  const weeks = Math.ceil((slotOf(days.at(-1) ?? first) + 1) / 7);
  const width = LEFT + weeks * (CELL + GAP);
  const height = TOP + 7 * (CELL + GAP);
  const cells = days.map((day) => {
    const slot = slotOf(day);
    const x = LEFT + Math.floor(slot / 7) * (CELL + GAP);
    const y = TOP + (slot % 7) * (CELL + GAP);
    const state = STATE_LABELS[day.state];
    return html`<rect x="${x}" y="${y}" width="${CELL}" height="${CELL}" rx="2" data-state="${stateAttr(day.state)}" data-when="${formatDay(day.start)}" data-label="${state}" data-card="${JSON.stringify(card(day))}"><title>${formatDay(day.start)}: ${state}</title></rect>`;
  });
  const months = days.flatMap((day, index) => {
    // Label each month's first day, and a partial first month only when it
    // leaves room before the next label.
    const date = new Date(day.start).getUTCDate();
    if (date !== 1 && (index !== 0 || date > 14)) return [];
    const x = LEFT + Math.floor(slotOf(day) / 7) * (CELL + GAP);
    return [html`<text class="st-heat-label" x="${x}" y="${TOP - 5}">${month.format(day.start)}</text>`];
  });
  const weekdays = [
    [1, "Mon"],
    [3, "Wed"],
    [5, "Fri"],
  ].map(
    ([row, name]) =>
      html`<text class="st-heat-label" x="0" y="${TOP + Number(row) * (CELL + GAP) + CELL - 2}">${name}</text>`,
  );
  return html`<svg class="st-heat" viewBox="0 0 ${width} ${height}" aria-hidden="false">
  ${months}${weekdays}
  <g role="img" tabindex="0" data-strip aria-label="${label}">${cells}</g>
</svg>`;
}

/** The UTC day starts of the `count` days ending today. */
export const lastDays = (now: number, count: number): number[] =>
  bucketStarts(startOfDay(now) - (count - 1) * DAY, count, DAY);
