/* Chart geometry shared by the SVG charts, ported from the ts-dashboard
   renderer (internal/ui/charts/charts.go). Coordinates are viewBox units
   with one decimal, so charts need no inline style under the strict CSP. */

export type Point = [number, number];

/** The region map's viewBox; hub and vantage positions are in its units. */
export const MAP_WIDTH = 136;
export const MAP_HEIGHT = 80;

export const coordinate = (value: number): string => value.toFixed(1);

/** The smallest of 1, 2 or 5 × 10ⁿ at or above `value`. */
export function niceCeiling(value: number): number {
  if (value <= 0) return 1;
  let magnitude = 1;
  while (magnitude <= value / 10) magnitude *= 10;
  for (const factor of [1, 2, 5, 10]) if (factor * magnitude >= value) return factor * magnitude;
  return 10 * magnitude;
}

/** A monotone cubic curve through the points: it never overshoots below a
    trough or above a peak between samples. */
export function smoothPath(points: readonly Point[]): string {
  const [first] = points;
  if (!first) return "";
  const move = `M ${coordinate(first[0])},${coordinate(first[1])}`;
  if (points.length < 3) {
    return [move, ...points.slice(1).map(([x, y]) => `L ${coordinate(x)},${coordinate(y)}`)].join(" ");
  }
  const n = points.length;
  const at = (index: number): Point => points[index] ?? first;
  const slopes = Array.from({ length: n - 1 }, (_, i) => (at(i + 1)[1] - at(i)[1]) / (at(i + 1)[0] - at(i)[0]));
  const tangents = Array.from({ length: n }, (_, i) => {
    if (i === 0) return slopes[0] ?? 0;
    if (i === n - 1) return slopes[n - 2] ?? 0;
    const before = slopes[i - 1] ?? 0;
    const after = slopes[i] ?? 0;
    return before * after > 0 ? 2 / (1 / before + 1 / after) : 0;
  });
  const curves: string[] = [];
  let straight: number | null = null;
  for (let i = 0; i < n - 1; i++) {
    const [x0, y0] = at(i);
    const [x1, y1] = at(i + 1);
    const slope = slopes[i] ?? 0;
    if (Math.abs((tangents[i] ?? 0) - slope) < 1e-9 && Math.abs((tangents[i + 1] ?? 0) - slope) < 1e-9) {
      if (straight !== null && Math.abs(straight - slope) < 1e-9) curves.pop();
      curves.push(`L ${coordinate(x1)},${coordinate(y1)}`);
      straight = slope;
      continue;
    }
    straight = null;
    const dx = (x1 - x0) / 3;
    curves.push(
      `C ${coordinate(x0 + dx)},${coordinate(y0 + (tangents[i] ?? 0) * dx)} ${coordinate(x1 - dx)},${coordinate(y1 - (tangents[i + 1] ?? 0) * dx)} ${coordinate(x1)},${coordinate(y1)}`,
    );
  }
  return [move, ...curves].join(" ");
}

/** Splits values into runs without gaps, keeping each value's index. */
export function runs(values: readonly (number | null)[]): { index: number; value: number }[][] {
  const result: { index: number; value: number }[][] = [];
  let run: { index: number; value: number }[] = [];
  values.forEach((value, index) => {
    if (value === null) {
      if (run.length > 0) result.push(run);
      run = [];
    } else run.push({ index, value });
  });
  if (run.length > 0) result.push(run);
  return result;
}

/** A short FNV-1a hash, for ids unique per chart. */
export function hash(text: string): string {
  let value = 2166136261;
  for (let index = 0; index < text.length; index++) value = Math.imul(value ^ text.charCodeAt(index), 16777619) >>> 0;
  return value.toString(16);
}
