/* Time ranges the status bars can show: 24 hourly bars, 28 six-hour bars
   over 7 days, or 30 and 90 daily bars. Every range ends with the bucket
   holding now and starts on a UTC boundary. */

import { DAY, HOUR } from "../format";

export const RANGES = {
  "24h": { label: "24 hours", short: "24h", count: 24, size: HOUR },
  "7d": { label: "7 days", short: "7d", count: 28, size: 6 * HOUR },
  "30d": { label: "30 days", short: "30d", count: 30, size: DAY },
  "90d": { label: "90 days", short: "90d", count: 90, size: DAY },
} as const;

export type RangeKey = keyof typeof RANGES;

export const RANGE_KEYS = Object.keys(RANGES) as RangeKey[];

export const DEFAULT_RANGE: RangeKey = "90d";

export const isRange = (value: string | undefined): value is RangeKey =>
  value !== undefined && Object.hasOwn(RANGES, value);

/** Hourly rollups back the ranges with sub-day buckets; the rest use days. */
export const byHour = (range: RangeKey): boolean => RANGES[range].size < DAY;

/** The starts of `count` consecutive buckets of `size`, from `start`. */
export const bucketStarts = (start: number, count: number, size: number): number[] =>
  Array.from({ length: count }, (_, index) => start + index * size);

/** Where the range's first bucket starts. */
export function rangeStart(range: RangeKey, now: number): number {
  const { count, size } = RANGES[range];
  return now - (now % size) - (count - 1) * size;
}
