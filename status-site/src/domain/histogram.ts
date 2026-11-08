/* Latency histograms. Hourly and daily rollups keep counts per fixed
   latency bucket, so percentiles can be read from any mix of rollups. */

/** Upper bounds of the buckets in milliseconds; a last bucket holds the rest. */
export const BOUNDS = [50, 100, 150, 200, 300, 400, 600, 800, 1_000, 1_500, 2_500, 5_000] as const;

export type Histogram = number[];

export const emptyHistogram = (): Histogram => Array.from({ length: BOUNDS.length + 1 }, () => 0);

export function merge(list: readonly Histogram[]): Histogram {
  const sum = emptyHistogram();
  for (const hist of list) hist.forEach((count, index) => (sum[index] = (sum[index] ?? 0) + count));
  return sum;
}

/** Parses a stored histogram, tolerating empty or malformed text. */
export function parseHistogram(text: string | null | undefined): Histogram {
  try {
    const value: unknown = JSON.parse(text ?? "[]");
    if (Array.isArray(value) && value.length === BOUNDS.length + 1 && value.every(Number.isFinite)) {
      return value as Histogram;
    }
  } catch {
    // Fall through to an empty histogram.
  }
  return emptyHistogram();
}

/** The latency below which a share `p` (0–1) of samples fell, interpolated
    within its bucket; null without samples. */
export function percentile(hist: Histogram, p: number): number | null {
  const count = hist.reduce((sum, value) => sum + value, 0);
  if (count === 0) return null;
  const target = Math.max(Math.min(p, 1), 0) * count;
  let seen = 0;
  for (let index = 0; index < hist.length; index++) {
    const inBucket = hist[index] ?? 0;
    if (inBucket === 0 || seen + inBucket < target) {
      seen += inBucket;
      continue;
    }
    const lower = index === 0 ? 0 : (BOUNDS[index - 1] ?? 0);
    // The last bucket has no measured upper bound, so it reports the top
    // bound instead of inventing one.
    const upper = BOUNDS[index] ?? lower;
    return Math.round(lower + ((target - seen) / inBucket) * (upper - lower));
  }
  return BOUNDS.at(-1) ?? null;
}

/** A SQL expression building the histogram of `latency` over rows where
    `when` holds, as a JSON array. Both arguments are spliced into the SQL
    text, so they must be trusted SQL fragments defined in code, never
    values taken from a request. */
export function histogramSql(latency: string, when: string): string {
  const buckets = BOUNDS.map(
    (bound, index) =>
      `sum(CASE WHEN ${when} AND ${latency} ${index === 0 ? "" : `> ${BOUNDS[index - 1]} AND ${latency} `}<= ${bound} THEN 1 ELSE 0 END)`,
  );
  buckets.push(`sum(CASE WHEN ${when} AND ${latency} > ${BOUNDS.at(-1)} THEN 1 ELSE 0 END)`);
  return `json_array(${buckets.join(", ")})`;
}

/** A SQL expression summing the stored histograms in `column`, bucket by
    bucket, as a JSON array. */
export function mergedHistogramSql(column: string): string {
  const buckets = Array.from(
    { length: BOUNDS.length + 1 },
    (_, index) => `coalesce(sum(json_extract(${column}, '$[${index}]')), 0)`,
  );
  return `json_array(${buckets.join(", ")})`;
}
