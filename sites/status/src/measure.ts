/* Measurements from per-vantage rollups: counts, latency percentiles and
   series of any set of endpoints from one vantage or the consensus. */

import type { VantagePeriod } from "./db/details";
import { percentile } from "./domain/histogram";
import { countsOf, sumCounts, type ProbeCounts } from "./domain/status";

export class Measurements {
  private readonly index = new Map<string, Map<number, ProbeCounts>>();

  constructor(periods: readonly VantagePeriod[]) {
    for (const period of periods) {
      const key = `${period.endpointId}\n${period.vantage}`;
      const entries = this.index.get(key) ?? new Map<number, ProbeCounts>();
      entries.set(Date.parse(period.start), countsOf(period));
      this.index.set(key, entries);
    }
  }

  /** Summed counts of the endpoints from `vantage` over periods [from, to)
      of `size` milliseconds. */
  counts(endpointIds: readonly string[], vantage: string, from: number, to: number, size: number): ProbeCounts {
    const list: ProbeCounts[] = [];
    for (const id of endpointIds) {
      const entries = this.index.get(`${id}\n${vantage}`);
      for (let at = from; at < to; at += size) {
        const counts = entries?.get(at);
        if (counts) list.push(counts);
      }
    }
    return sumCounts(list);
  }

  /** A latency percentile per bucket of `bucket` milliseconds starting at
      each of `starts`, summed from periods of `period` milliseconds. */
  series(
    endpointIds: readonly string[],
    vantage: string,
    starts: readonly number[],
    bucket: number,
    period: number,
    p: number,
  ): (number | null)[] {
    return starts.map((start) => percentile(this.counts(endpointIds, vantage, start, start + bucket, period).hist, p));
  }
}
