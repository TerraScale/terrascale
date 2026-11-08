/* Status badges: a small flat SVG naming a component, or the platform, and
   its current state, for READMEs and dashboards. */

import { STATE_LABELS, type TimelineState } from "../domain/status";
import { escape } from "../html";
import { hash } from "../views/charts/geometry";

const COLORS: Record<TimelineState, string> = {
  operational: "#34d399",
  maintenance: "#60a5fa",
  degraded: "#fbbf24",
  partial_outage: "#fb923c",
  major_outage: "#f87171",
  none: "#6c7589",
};

/** Text width in pixels at 11px Verdana-like sans, as badge renderers estimate. */
const width = (text: string): number => Math.round(text.length * 6.4 + 12);

export function badge(label: string, state: TimelineState, message = STATE_LABELS[state]): string {
  const left = width(label);
  const right = width(message);
  const total = left + right;
  // Unique per badge, so badges inlined into one document never share a clip.
  const clip = `ts-badge-${hash(`${label}:${message}`)}`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${total}" height="20" role="img" aria-label="${escape(`${label}: ${message}`)}">
<title>${escape(`${label}: ${message}`)}</title>
<clipPath id="${clip}"><rect width="${total}" height="20" rx="3"/></clipPath>
<g clip-path="url(#${clip})"><rect width="${left}" height="20" fill="#1e2330"/><rect x="${left}" width="${right}" height="20" fill="${COLORS[state]}"/></g>
<g font-family="Verdana,DejaVu Sans,sans-serif" font-size="11" text-anchor="middle">
<text x="${left / 2}" y="14" fill="#eef1f7">${escape(label)}</text>
<text x="${left + right / 2}" y="14" fill="#0a0c12">${escape(message)}</text>
</g>
</svg>
`;
}
