import { DatabaseSync } from "node:sqlite";
import { describe, expect, it } from "vitest";
import { BOUNDS, emptyHistogram, histogramSql, merge, parseHistogram, percentile } from "./histogram";

function histogramOf(latencies: readonly number[]): number[] {
  const hist = emptyHistogram();
  for (const latency of latencies) {
    const index = BOUNDS.findIndex((bound) => latency <= bound);
    const at = index === -1 ? BOUNDS.length : index;
    hist[at] = (hist[at] ?? 0) + 1;
  }
  return hist;
}

describe("percentile", () => {
  it("interpolates inside the bucket holding the rank", () => {
    const hist = histogramOf([10, 20, 30, 40, 60, 70, 80, 90, 120, 450]);
    expect(percentile(hist, 0.5)).toBe(63);
    expect(percentile(hist, 0.9)).toBe(150);
    expect(percentile(hist, 1)).toBe(600);
  });

  it("reports the top bound for samples above it", () => {
    expect(percentile(histogramOf([9_000, 12_000]), 0.5)).toBe(BOUNDS.at(-1));
  });

  it("returns null without samples", () => {
    expect(percentile(emptyHistogram(), 0.5)).toBeNull();
  });
});

describe("histograms", () => {
  it("merges bucket by bucket", () => {
    expect(merge([histogramOf([10]), histogramOf([10, 700])]).slice(0, 8)).toEqual([2, 0, 0, 0, 0, 0, 0, 1]);
  });

  it("parses stored text defensively", () => {
    expect(parseHistogram("[1,2]")).toEqual(emptyHistogram());
    expect(parseHistogram("nope")).toEqual(emptyHistogram());
    expect(parseHistogram(JSON.stringify(histogramOf([60])))).toEqual(histogramOf([60]));
  });

  it("builds the same histogram in SQL", () => {
    const db = new DatabaseSync(":memory:");
    db.exec("CREATE TABLE t (ok INTEGER, ms INTEGER)");
    const latencies = [5, 50, 51, 100, 999, 1_000, 4_000, 6_000];
    for (const ms of latencies) db.prepare("INSERT INTO t VALUES (1, ?)").run(ms);
    db.prepare("INSERT INTO t VALUES (0, 20)").run();
    const row = db.prepare(`SELECT ${histogramSql("ms", "ok = 1")} AS hist FROM t`).get() as { hist: string };
    expect(JSON.parse(row.hist)).toEqual(histogramOf(latencies));
  });
});
