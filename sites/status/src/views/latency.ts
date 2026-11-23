/* Latency charts of a set of endpoints over a range: the median and p95 of
   the consensus with deploy markers, and the median seen from each vantage. */

import type { Deploy } from "../db/details";
import { bucketStarts, byHour, RANGES, rangeStart, type RangeKey } from "../domain/range";
import { DAY, formatClock, formatDay, HOUR } from "../format";
import { html, type Html } from "../html";
import type { Measurements } from "../measure";
import { vantageLabel, type Coverage } from "../domain/coverage";
import { VANTAGES } from "../probe/vantages";
import { chartLegend, lineChart } from "./charts/line";
import { empty } from "./components";

interface Axis {
  starts: number[];
  size: number;
  period: number;
  points: string[];
  ticks: string[];
}

function axis(range: RangeKey, now: number): Axis {
  const { count, size } = RANGES[range];
  const start = rangeStart(range, now);
  const starts = bucketStarts(start, count, size);
  const hourly = byHour(range);
  const name = (at: number) => (hourly ? `${formatDay(at)} ${formatClock(at)} UTC` : formatDay(at));
  const ticks = [0, 1, 2, 3, 4].map((step) => name(starts[Math.round((step * (starts.length - 1)) / 4)] ?? start));
  return { starts, size, period: hourly ? HOUR : DAY, points: starts.map(name), ticks };
}

const format = (value: number) => `${Math.round(value)} ms`;

export function latencyCharts(
  measurements: Measurements,
  endpointIds: readonly string[],
  deploys: readonly Deploy[],
  range: RangeKey,
  now: number,
  coverage: Coverage,
): { summary: Html; vantages: Html } {
  const { starts, size, period, points, ticks } = axis(range, now);
  const first = starts[0] ?? now;
  const p50 = measurements.series(endpointIds, "all", starts, size, period, 0.5);
  const p95 = measurements.series(endpointIds, "all", starts, size, period, 0.95);
  const markers = deploys
    .filter((deploy) => endpointIds.includes(deploy.endpointId))
    .map((deploy) => ({ index: Math.floor((Date.parse(deploy.at) - first) / size), label: deploy.version }));
  const label = RANGES[range].label;
  const none = empty("No latency yet", "Latency appears once health checks have run for a while.");

  const summaryChart = lineChart(
    { label: `Median and p95 latency over the last ${label}`, ticks, points, format, markers },
    [
      { name: "Median", values: p50 },
      { name: "p95", values: p95 },
    ],
  );
  const perVantage = VANTAGES.map((vantage) => ({
    name: vantageLabel(coverage, vantage),
    values: measurements.series(endpointIds, vantage.code, starts, size, period, 0.5),
  })).filter((entry) => entry.values.some((value) => value !== null));
  const vantageChart = lineChart(
    { label: `Median latency from each vantage over the last ${label}`, ticks, points, format },
    perVantage,
  );

  // Both charts show data only once the consensus has any, so they agree.
  if (!p50.some((value) => value !== null)) return { summary: none, vantages: none };
  return {
    summary: html`${summaryChart}${chartLegend(["Median", "p95"])}`,
    vantages:
      vantageChart.value === "" ? none : html`${vantageChart}${chartLegend(perVantage.map((entry) => entry.name))}`,
  };
}
