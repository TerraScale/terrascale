/* Rollups and retention, run by the minute cron. Hours are rebuilt from the
   samples and days from the hours, each replacing its row outright, so a
   rollup can run any number of times. Every statement is bound to one
   endpoint and reads only that endpoint's rows. */

import { sqlTime } from "../db/reads";
import { MIN_VANTAGES } from "../domain/consensus";
import { histogramSql, mergedHistogramSql } from "../domain/histogram";
import type { Env } from "../env";
import { DAY, HOUR, startOfDay, startOfHour } from "../format";
import { DAY_RETENTION_MS, HOUR_RETENTION_MS, SAMPLE_RETENTION_MS } from "./retention";

/** The previous hour and day are rolled once more this long after they end. */
const SETTLE_MS = 3 * 60_000;

/** Days are refreshed every this many minutes while they are open. */
const DAY_EVERY_MINUTES = 10;

// The rule of isDown in domain/consensus: a round is judged with enough
// answers, and down when most of them failed. A canary (src/probe/canary.ts)
// is one authenticated check run by the coordinator, so its every round is
// judged on its own, unless it was not configured (no checks at all).
const JUDGED = `(total >= ${MIN_VANTAGES} OR (total > 0 AND (SELECT kind FROM endpoints WHERE id = ?1) = 'canary'))`;
const DOWN = "(total - ok) * 2 > total";

// Rounds before an endpoint's launch say nothing about its uptime.
const LAUNCHED = "at >= (SELECT live_since FROM endpoints WHERE id = ?1)";

const ROLL_ALL = `INSERT OR REPLACE INTO probe_hours
    (endpoint_id, hour, vantage, checks, failures, degraded, latency_sum, latency_max, hist)
  SELECT endpoint_id, ?2, 'all', count(*), sum(${DOWN}), sum(state = 'degraded'),
    coalesce(sum(CASE WHEN NOT ${DOWN} THEN latency_ms END), 0),
    coalesce(max(CASE WHEN NOT ${DOWN} THEN latency_ms END), 0),
    ${histogramSql("latency_ms", `NOT ${DOWN}`)}
  FROM probe_samples WHERE endpoint_id = ?1 AND at >= ?2 AND at < ?3 AND ${LAUNCHED} AND ${JUDGED}
  HAVING count(*) > 0`;

const ROLL_VANTAGES = `INSERT OR REPLACE INTO probe_hours
    (endpoint_id, hour, vantage, checks, failures, degraded, latency_sum, latency_max, hist)
  SELECT ?1, ?2, vantage, count(*), sum(ok = 0), 0,
    coalesce(sum(CASE WHEN ok = 1 THEN ms END), 0),
    coalesce(max(CASE WHEN ok = 1 THEN ms END), 0),
    ${histogramSql("ms", "ok = 1")}
  FROM (
    SELECT json_extract(result.value, '$.v') AS vantage, json_extract(result.value, '$.ok') AS ok,
      json_extract(result.value, '$.ms') AS ms
    FROM probe_samples, json_each(probe_samples.results) AS result
    WHERE endpoint_id = ?1 AND at >= ?2 AND at < ?3 AND ${LAUNCHED}
  ) WHERE ok IS NOT NULL GROUP BY vantage`;

const ROLL_DAY = `INSERT OR REPLACE INTO probe_days
    (endpoint_id, day, vantage, checks, failures, degraded, latency_sum, latency_max, hist)
  SELECT ?1, ?2, vantage, sum(checks), sum(failures), sum(degraded), sum(latency_sum), max(latency_max),
    ${mergedHistogramSql("hist")}
  FROM probe_hours WHERE endpoint_id = ?1 AND hour >= ?2 AND hour < ?3 GROUP BY vantage`;

/** The statements that roll up and prune every endpoint at `now`. */
export function rollupStatements(db: D1Database, endpointIds: readonly string[], now: number): D1PreparedStatement[] {
  const hour = startOfHour(now);
  const day = startOfDay(now);
  const hours = now - hour < SETTLE_MS ? [hour - HOUR, hour] : [hour];
  const days: number[] = [];
  if (now - day < HOUR + SETTLE_MS) days.push(day - DAY);
  if (Math.floor(now / 60_000) % DAY_EVERY_MINUTES === 0 || now - hour < SETTLE_MS) days.push(day);
  // Prune once a day, just after the previous day settles.
  const prune = now - day >= SETTLE_MS && now - day < SETTLE_MS + 60_000;

  return endpointIds.flatMap((id) => [
    ...hours.flatMap((from) => [
      db.prepare(ROLL_ALL).bind(id, sqlTime(from), sqlTime(from + HOUR)),
      db.prepare(ROLL_VANTAGES).bind(id, sqlTime(from), sqlTime(from + HOUR)),
    ]),
    ...days.map((from) => db.prepare(ROLL_DAY).bind(id, sqlTime(from), sqlTime(from + DAY))),
    ...(prune
      ? [
          db
            .prepare("DELETE FROM probe_samples WHERE endpoint_id = ?1 AND at < ?2")
            .bind(id, sqlTime(now - SAMPLE_RETENTION_MS)),
          db
            .prepare("DELETE FROM probe_hours WHERE endpoint_id = ?1 AND hour < ?2")
            .bind(id, sqlTime(now - HOUR_RETENTION_MS)),
          db
            .prepare("DELETE FROM probe_days WHERE endpoint_id = ?1 AND day < ?2")
            .bind(id, sqlTime(now - DAY_RETENTION_MS)),
        ]
      : []),
  ]);
}

/** Rolls up and prunes every endpoint, planned ones included. */
export async function rollup(env: Env, now: number): Promise<void> {
  const { results } = await env.STATUS_DB.prepare("SELECT id FROM endpoints").all<{ id: string }>();
  const statements = rollupStatements(
    env.STATUS_DB,
    (results ?? []).map((row) => row.id),
    now,
  );
  if (statements.length > 0) await env.STATUS_DB.batch(statements);
}
