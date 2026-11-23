/* A server-rendered line chart: gridlines, axis labels, one line and one flat
   translucent area per series, and a focusable hover column per sample whose
   native tooltip works without scripts. Each column also
   carries its tooltip as data-tip JSON for the chart client script, which
   swaps the SVG tooltip for a frosted HTML one, and refits the x coordinates
   to the chart's real width so text stays 11px and the plot 220px tall.
   Gaps break lines and areas. */

import { html, type Html } from "../../html";
import { coordinate, niceCeiling, runs, smoothPath } from "./geometry";

export interface Series {
  name: string;
  values: readonly (number | null)[];
}

export interface LineOptions {
  /** The chart's accessible name. */
  label: string;
  /** Five x-axis labels, from the first sample to the last. */
  ticks: readonly string[];
  /** Every sample's name, shown as its tooltip header. */
  points: readonly string[];
  /** Formats values in tooltips and on the y axis. */
  format: (value: number) => string;
  height?: number;
  /** Vertical markers, such as deploys, at sample indexes. */
  markers?: readonly { index: number; label: string }[];
}

/** The viewBox width the server draws at, and the plot's side margins. */
export const WIDTH = 720;
export const LEFT = 56;
export const RIGHT = 12;
const TOP = 14;
const BOTTOM = 32;

/** Series colors in the categorical palette (--ts-cat-1 to 8 in tokens.css).
    Beyond this many series colors repeat, so callers should aggregate first. */
export const CATEGORIES = 5;

/** The class coloring a series by its index. */
const category = (seriesIndex: number): string => `ts-cat-${(seriesIndex % CATEGORIES) + 1}`;

/** X-axis label anchors: the ends align inward so labels stay in the chart. */
const TICK_ANCHORS = ["start", "middle", "middle", "middle", "end"] as const;

/** The width of a marker label's character at its 10px size, in chart units. */
const MARKER_CHAR = 6;

/** Hover targets rendered per chart; dense series share columns. */
export const MAX_COLUMNS = 60;

interface TipRow {
  name: string;
  value: string;
  series: string;
}

/** Renders the chart, or nothing when there is no value to draw. */
export function lineChart(options: LineOptions, series: readonly Series[]): Html {
  const height = options.height ?? 220;
  const count = series[0]?.values.length ?? 0;
  const all = series.flatMap((entry) => entry.values.filter((value): value is number => value !== null));
  if (count === 0 || all.length === 0 || series.some((entry) => entry.values.length !== count)) return html``;

  const maximum = Math.max(...all);
  const scale = niceCeiling(maximum);
  const plotWidth = WIDTH - LEFT - RIGHT;
  const plotHeight = height - TOP - BOTTOM;
  const bottom = TOP + plotHeight;
  const xAt = (index: number) => LEFT + (plotWidth * index) / Math.max(1, count - 1);
  const yAt = (value: number) => TOP + plotHeight * (1 - Math.max(0, value) / scale);
  // Overlapping areas: the largest is drawn first and lighter so smaller ones
  // keep their color on top; beyond two series every area stays faint.
  const mean = (values: readonly (number | null)[]) => {
    const known = values.filter((value): value is number => value !== null);
    return known.reduce((sum, value) => sum + value, 0) / Math.max(1, known.length);
  };
  const order = series
    .map((entry, seriesIndex) => ({ seriesIndex, mean: mean(entry.values) }))
    .sort((a, b) => b.mean - a.mean)
    .map((entry) => entry.seriesIndex);
  let overlap = "";
  if (series.length === 2) overlap = " is-overlap";
  else if (series.length > 2) overlap = " is-many";

  const grid = Array.from({ length: 5 }, (_, step) => {
    const y = coordinate(TOP + (plotHeight * step) / 4);
    return html`<line class="ts-chart-grid${step === 4 ? " ts-chart-baseline" : ""}" x1="${LEFT}" y1="${y}" x2="${WIDTH - RIGHT}" y2="${y}"></line>
  <text class="ts-chart-axis" x="${LEFT - 10}" y="${y}" dy="0.32em" text-anchor="end">${options.format((scale * (4 - step)) / 4)}</text>`;
  });
  const ticks = Array.from({ length: 5 }, (_, step) => {
    const index = count > 1 ? Math.round((step * (count - 1)) / 4) : 0;
    const anchor = TICK_ANCHORS[step] ?? "middle";
    return html`<text class="ts-chart-axis ts-chart-tick" x="${coordinate(xAt(index))}" y="${height - 8}" text-anchor="${anchor}">${options.ticks[step] ?? ""}</text>`;
  });
  const lineOf = (run: ReturnType<typeof runs>[number]) =>
    smoothPath(run.map(({ index, value }) => [xAt(index), yAt(value)]));
  const areas = order.map((seriesIndex, rank) =>
    runs(series[seriesIndex]?.values ?? []).map((run) => {
      const first = run[0]?.index ?? 0;
      const last = run.at(-1)?.index ?? 0;
      return html`<path class="ts-chart-area ${category(seriesIndex)}${rank === 0 && overlap ? " ts-chart-back" : ""}" d="${lineOf(run)} L ${coordinate(xAt(last))},${bottom} L ${coordinate(xAt(first))},${bottom} Z"></path>`;
    }),
  );
  const paths = series.map((entry, seriesIndex) =>
    runs(entry.values).map(
      (run) => html`<path class="ts-chart-line ${category(seriesIndex)}" d="${lineOf(run)}"></path>`,
    ),
  );

  const step = Math.max(1, Math.ceil(count / MAX_COLUMNS));
  const columnWidth = (plotWidth * step) / Math.max(1, count - 1);
  const columns: Html[] = [];
  for (let index = Math.floor(step / 2); index < count; index += step) {
    const x = xAt(index);
    const left = Math.max(LEFT, x - columnWidth / 2);
    const right = Math.min(WIDTH - RIGHT, x + columnWidth / 2);
    const rows = series.map((entry, seriesIndex): TipRow => {
      const value = entry.values[index] ?? null;
      return { name: entry.name, value: value === null ? "—" : options.format(value), series: category(seriesIndex) };
    });
    const header = options.points[index] ?? "";
    const label = `${header ? `${header}: ` : ""}${rows.map((row) => `${row.name} ${row.value}`).join(", ")}`;
    const dots = series.flatMap((entry, seriesIndex) => {
      const value = entry.values[index] ?? null;
      return value === null
        ? []
        : [
            html`<circle class="ts-chart-dot ${category(seriesIndex)}" cx="${coordinate(x)}" cy="${coordinate(yAt(value))}" r="4"></circle>`,
          ];
    });
    const data = JSON.stringify({ head: header, rows: rows.map((row) => [row.name, row.value, row.series]) });
    columns.push(html`<g class="ts-chart-column" tabindex="0" role="img" data-tip="${data}">
  <rect class="ts-chart-hit" x="${coordinate(left)}" y="${TOP}" width="${coordinate(right - left)}" height="${plotHeight}"></rect>
  <line class="ts-chart-guide" x1="${coordinate(x)}" y1="${TOP}" x2="${coordinate(x)}" y2="${bottom}"></line>
  ${dots}
  <title>${label}</title>
</g>`);
  }

  // Markers sharing a sample merge into one label without repeats. Labels
  // stay inside the plot, and one that would collide with a newer label is
  // left to its tooltip.
  const labels = new Map<number, Set<string>>();
  for (const marker of options.markers ?? []) {
    if (marker.index >= 0 && marker.index < count) {
      labels.set(marker.index, (labels.get(marker.index) ?? new Set()).add(marker.label));
    }
  }
  let free = Infinity; // where the newest label placed so far starts
  const markers = [...labels]
    .sort(([a], [b]) => b - a)
    .map(([index, list]) => {
      const x = xAt(index);
      const label = [...list].join(", ");
      const width = label.length * MARKER_CHAR;
      const start = Math.max(LEFT, Math.min(x - width / 2, WIDTH - RIGHT - width));
      const fits = start + width + MARKER_CHAR <= free;
      if (fits) free = start;
      return html`<g class="st-chart-marker"><line x1="${coordinate(x)}" y1="${TOP}" x2="${coordinate(x)}" y2="${bottom}"></line>${
        fits ? html`<text x="${coordinate(start)}" y="${TOP - 3}">${label}</text>` : ""
      }<title>${label}</title></g>`;
    });

  return html`<svg class="ts-chart" viewBox="0 0 ${WIDTH} ${height}" role="group" aria-label="${options.label}" data-chart>
  ${grid}${ticks}${markers}<g class="ts-chart-areas${overlap}">${areas}</g>${paths}${columns}
</svg>`;
}

/** The legend under a multi-series chart. */
export function chartLegend(names: readonly string[]): Html {
  return html`<ul class="ts-chart-legend" aria-hidden="true">${names.map(
    (name, index) =>
      html`<li class="ts-chart-legend-item"><span class="ts-chart-swatch ${category(index)}"></span>${name}</li>`,
  )}</ul>`;
}
