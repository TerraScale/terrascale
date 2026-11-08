/* The outbox. The minute cron enqueues every eligible incident update for
   every confirmed subscriber that follows its components, then delivers
   what is due, retrying failures with exponential backoff. Each run first
   claims the messages it sends, counting the attempt and leasing them, so
   overlapping runs never send one twice and a run that dies leaves them to
   be retried once the lease ends.

   Updates of automatic incidents wait five minutes, and an automatic
   incident resolved within five minutes notifies nobody: a blip should
   not wake anyone. Operator incidents notify at once. */

import { newId } from "../ids";

import { iso, sqlTime } from "../db/reads";
import type { IncidentImpact, IncidentKind, IncidentStatus } from "../domain/status";
import type { Env } from "../env";
import { email, webhook, type Notice } from "./messages";
import { sign, webhookKey } from "./tokens";
import { post } from "./webhooks";

/** Automatic incidents notify once they have lasted this long. */
export const AUTO_HOLD_MS = 5 * 60_000;

/** Updates older than this are never enqueued. */
const LOOKBACK_MS = 2 * 86_400_000;

/** Attempts before a message is given up. */
export const MAX_ATTEMPTS = 8;

/** Messages delivered per cron run. */
const BATCH = 20;

/** Fan-out per cron run; later runs pick up pairs not yet queued. */
const ENQUEUE_BATCH = 1_000;

/** How long a claimed message stays with its run before it is due again. */
const LEASE_MS = 10 * 60_000;

const ELIGIBLE = `SELECT s.id AS subscriberId, u.id AS updateId
  FROM incident_updates u
  JOIN incidents i ON i.id = u.incident_id
  JOIN subscribers s ON s.confirmed_at IS NOT NULL AND s.confirmed_at <= u.created_at
  WHERE u.created_at >= ?1
    AND (i.source = 'manual' OR i.operator_touched = 1
      OR (i.created_at <= ?2 AND (i.resolved_at IS NULL OR i.resolved_at >= datetime(i.created_at, '+5 minutes'))))
    AND (s.components = '[]'
      OR NOT EXISTS (SELECT 1 FROM incident_components c WHERE c.incident_id = i.id)
      OR EXISTS (SELECT 1 FROM incident_components c, json_each(s.components) j
        WHERE c.incident_id = i.id AND c.component_id = j.value))
    AND NOT EXISTS (SELECT 1 FROM outbox o WHERE o.subscriber_id = s.id AND o.update_id = u.id)
  ORDER BY u.created_at, u.id, s.id LIMIT ?3`;

// Claims up to a batch of due messages for run ?4, or only the pending
// confirmation of subscriber ?5 when it is set.
const CLAIM = `UPDATE outbox SET claim = ?4, attempts = attempts + 1, next_attempt_at = ?6
  WHERE id IN (SELECT id FROM outbox
    WHERE sent_at IS NULL AND next_attempt_at <= ?1 AND attempts < ?2
      AND (?5 IS NULL OR (subscriber_id = ?5 AND kind = 'confirm'))
    ORDER BY created_at LIMIT ?3)`;

const CLAIMED = `SELECT o.id, o.kind, o.attempts, s.id AS subscriberId, s.channel, s.target, s.flavor,
    u.status, u.body, ${iso("u.created_at")} AS updateAt,
    i.id AS incidentId, i.kind AS incidentKind, i.title, i.impact, i.source
  FROM outbox o JOIN subscribers s ON s.id = o.subscriber_id
  LEFT JOIN incident_updates u ON u.id = o.update_id
  LEFT JOIN incidents i ON i.id = u.incident_id
  WHERE o.claim = ?1 AND o.sent_at IS NULL
  ORDER BY o.created_at`;

interface Due {
  id: string;
  kind: "confirm" | "update";
  attempts: number;
  subscriberId: string;
  channel: "email" | "webhook";
  target: string;
  flavor: Notice["flavor"];
  status: IncidentStatus | null;
  body: string | null;
  updateAt: string | null;
  incidentId: string | null;
  incidentKind: IncidentKind | null;
  title: string | null;
  impact: IncidentImpact | null;
  source: "manual" | "auto" | null;
}

export interface Delivery {
  fetcher?: typeof fetch;
}

/** Sender of every subscription message; the domain is onboarded for Email Sending. */
export const SENDER = { email: "noreply@terrascale.tech", name: "TerraScale Status" };

/** The links a subscriber's messages carry. */
export async function links(env: Env & { SUBSCRIPTION_SECRET: string }, subscriberId: string) {
  const origin = env.PUBLIC_ORIGIN ?? "https://status.terrascale.tech";
  const id = encodeURIComponent(subscriberId);
  return {
    site: origin,
    confirm: `${origin}/subscribe/confirm?id=${id}&token=${await sign(env.SUBSCRIPTION_SECRET, "confirm", subscriberId)}`,
    manage: `${origin}/subscribe/manage?id=${id}&token=${await sign(env.SUBSCRIPTION_SECRET, "manage", subscriberId)}`,
  };
}

async function deliver(env: Env & { SUBSCRIPTION_SECRET: string }, row: Due, delivery: Delivery): Promise<void> {
  const origin = env.PUBLIC_ORIGIN ?? "https://status.terrascale.tech";
  const notice: Notice = {
    kind: row.kind,
    channel: row.channel,
    flavor: row.flavor,
    links: {
      ...(await links(env, row.subscriberId)),
      incident: row.incidentId ? `${origin}/incidents/${row.incidentId}` : undefined,
    },
    incident:
      row.incidentId && row.incidentKind && row.title && row.impact && row.source
        ? { id: row.incidentId, kind: row.incidentKind, title: row.title, impact: row.impact, source: row.source }
        : undefined,
    update:
      row.status && row.body && row.updateAt
        ? { status: row.status, body: row.body, createdAt: row.updateAt }
        : undefined,
  };
  if (row.kind === "update" && (!notice.incident || !notice.update)) throw new Error("Update no longer exists");
  if (row.channel === "email") {
    await env.EMAIL.send({ from: SENDER, to: row.target, ...email(notice) });
  } else {
    const request = await webhook(notice, await webhookKey(env.SUBSCRIPTION_SECRET, row.subscriberId));
    await post(row.target, request.body, request.headers, delivery.fetcher);
  }
}

/** Claims due messages and delivers them, recording each result as soon as
    it is known. Returns how many were sent and how many failed. */
export async function deliverDue(
  env: Env,
  now: number,
  delivery: Delivery,
  only: string | null = null,
): Promise<{ sent: number; failed: number }> {
  const secret = env.SUBSCRIPTION_SECRET;
  if (!secret) return { sent: 0, failed: 0 };
  const signed = { ...env, SUBSCRIPTION_SECRET: secret };
  const run = newId();
  const [, claimed] = await env.STATUS_DB.batch([
    env.STATUS_DB.prepare(CLAIM).bind(sqlTime(now), MAX_ATTEMPTS, BATCH, run, only, sqlTime(now + LEASE_MS)),
    env.STATUS_DB.prepare(CLAIMED).bind(run),
  ]);
  const rows = (claimed?.results ?? []) as Due[];
  const results = await Promise.all(
    rows.map(async (row) => {
      try {
        await deliver(signed, row, delivery);
        await env.STATUS_DB.prepare("UPDATE outbox SET sent_at = ?2, claim = NULL, last_error = NULL WHERE id = ?1")
          .bind(row.id, sqlTime(now))
          .run();
        return true;
      } catch (error) {
        // Attempts were counted when claimed: back off 1, 2, 4… minutes, up to six hours.
        const retry = sqlTime(now + Math.min(2 ** (row.attempts - 1), 360) * 60_000);
        const message = error instanceof Error ? error.message.slice(0, 300) : "Delivery failed";
        await env.STATUS_DB.prepare(
          "UPDATE outbox SET next_attempt_at = ?2, claim = NULL, last_error = ?3 WHERE id = ?1",
        )
          .bind(row.id, retry, message)
          .run();
        return false;
      }
    }),
  );
  const sent = results.filter(Boolean).length;
  return { sent, failed: results.length - sent };
}

/** Enqueues eligible updates, delivers up to a batch of due messages, and
    prunes old ones. Returns how many were sent and how many failed. */
export async function runOutbox(env: Env, now: number, delivery: Delivery): Promise<{ sent: number; failed: number }> {
  if (!env.SUBSCRIPTION_SECRET) return { sent: 0, failed: 0 };
  const { results } = await env.STATUS_DB.prepare(ELIGIBLE)
    .bind(sqlTime(now - LOOKBACK_MS), sqlTime(now - AUTO_HOLD_MS), ENQUEUE_BATCH)
    .all<{ subscriberId: string; updateId: string }>();
  if (results.length) {
    await env.STATUS_DB.prepare(
      `INSERT OR IGNORE INTO outbox (id, subscriber_id, kind, update_id, next_attempt_at, created_at)
       SELECT json_extract(q.value, '$.id'), s.id, 'update', u.id, ?2, ?2
       FROM json_each(?1) q
       JOIN subscribers s ON s.id = json_extract(q.value, '$.subscriberId')
       JOIN incident_updates u ON u.id = json_extract(q.value, '$.updateId')`,
    )
      .bind(JSON.stringify(results.map((pair) => ({ id: newId(), ...pair }))), sqlTime(now))
      .run();
  }
  const result = await deliverDue(env, now, delivery);
  // Retention: delivered messages for 30 days, unconfirmed subscribers for 7.
  await env.STATUS_DB.batch([
    env.STATUS_DB.prepare("DELETE FROM outbox WHERE sent_at IS NOT NULL AND sent_at < ?1").bind(
      sqlTime(now - 30 * 86_400_000),
    ),
    env.STATUS_DB.prepare("DELETE FROM subscribers WHERE confirmed_at IS NULL AND created_at < ?1").bind(
      sqlTime(now - 7 * 86_400_000),
    ),
  ]);
  return result;
}
