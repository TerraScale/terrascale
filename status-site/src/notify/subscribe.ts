/* The subscription endpoints: subscribe (POST form), confirm (a signed link
   opening a POST form), manage and unsubscribe, including RFC 8058
   one-click unsubscribe from mail clients. Responses never reveal whether
   an address is already subscribed. */

import { newId, isId } from "../ids";

import type { Component } from "../domain/status";
import type { Env } from "../env";
import { sqlTime } from "../db/reads";
import type { Page } from "../views/layout";
import {
  confirmedPage,
  confirmPage,
  invalidLinkPage,
  managePage,
  sentPage,
  subscribeForm,
  unavailablePage,
  unsubscribedPage,
} from "../views/subscribe";
import { links, MAX_ATTEMPTS } from "./outbox";
import { verify } from "./tokens";
import { isEmail, webhookFlavor } from "./webhooks";

/** A page to render, and the subscriber whose confirmation to send now. */
export type Answer = { page: Page; status: number; deliver?: string } | Response;

/** A new confirmation for the same target waits this long after the last. */
const CONFIRM_COOLDOWN_MS = 60 * 60_000;

/** Confirmations sent to one unconfirmed target at most. */
const MAX_CONFIRMATIONS = 3;

/** The rate limit key: the client address, or its /64 for IPv6, where one
    client often holds the whole prefix. */
export function clientKey(request: Request): string {
  const ip = request.headers.get("cf-connecting-ip") ?? "unknown";
  if (!ip.includes(":")) return ip;
  const [head = "", tail = ""] = ip.split("::");
  const groups = head.split(":").filter(Boolean);
  const rest = tail.split(":").filter(Boolean);
  while (groups.length + rest.length < 8) groups.push("0");
  return `${[...groups, ...rest].slice(0, 4).join(":")}::/64`;
}

const COMPONENTS = "SELECT id, group_id AS groupId, name, description, lifecycle FROM components ORDER BY position, id";

async function components(env: Env): Promise<Component[]> {
  const { results } = await env.STATUS_DB.prepare(COMPONENTS).all<Component>();
  return results ?? [];
}

async function fields(request: Request): Promise<URLSearchParams> {
  const type = request.headers.get("content-type") ?? "";
  if (!type.includes("application/x-www-form-urlencoded")) return new URLSearchParams();
  const text = await request.text();
  return new URLSearchParams(text.slice(0, 8_000));
}

async function subscribe(env: Env, request: Request, now: number): Promise<Answer> {
  const catalog = await components(env);
  const form = await fields(request);
  const channel = form.get("channel") === "email" ? "email" : "webhook";
  const target = (form.get("target") ?? "").trim();
  const supplied = [...new Set(form.getAll("components"))];
  const chosen = supplied.filter((id) => catalog.some((component) => component.id === id));
  const retry = (error: string, status = 400): Answer => ({
    page: subscribeForm({ components: catalog, error, values: { channel, target, components: chosen } }),
    status,
  });

  if (supplied.some((id) => !isId(id) || !catalog.some((component) => component.id === id)))
    return retry("Choose valid component IDs from the catalog.");

  // Every accepted request answers the same, so nobody can tell whether a
  // target is already subscribed.
  const sent: Answer = { page: sentPage(channel), status: 202 };
  // Bots fill every field; people never see this one.
  if (form.get("website")) return sent;
  if (env.SUBSCRIBE_LIMITER && !(await env.SUBSCRIBE_LIMITER.limit({ key: clientKey(request) })).success) {
    return retry("Too many attempts. Wait a minute and try again.", 429);
  }

  let flavor: "email" | "discord" | "slack" | "generic";
  if (channel === "email") {
    if (!isEmail(target)) return retry("Enter a valid email address.");
    flavor = "email";
  } else {
    const found = webhookFlavor(target);
    if (!found) return retry("Enter an HTTPS webhook URL: a Discord or Slack webhook, or any public HTTPS endpoint.");
    flavor = found;
  }

  const normalized = channel === "email" ? target.toLowerCase() : target;
  const time = sqlTime(now);
  const existing = await env.STATUS_DB.prepare(
    "SELECT id, confirmed_at AS confirmedAt FROM subscribers WHERE channel = ?1 AND target = ?2",
  )
    .bind(channel, normalized)
    .first<{ id: string; confirmedAt: string | null }>();
  if (existing?.confirmedAt) return sent;

  const id = existing?.id ?? newId();
  const [, queued] = await env.STATUS_DB.batch([
    // Asking again restarts the unconfirmed subscriber's week to confirm.
    env.STATUS_DB.prepare(
      `INSERT INTO subscribers (id, channel, target, flavor, components, created_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6)
        ON CONFLICT (channel, target) DO UPDATE SET components = excluded.components,
          created_at = CASE WHEN confirmed_at IS NULL THEN excluded.created_at ELSE created_at END`,
    ).bind(id, channel, normalized, flavor, JSON.stringify(chosen), time),
    // One pending confirmation at a time, at most one an hour, and only a
    // few in all, so the form cannot be used to flood someone else.
    env.STATUS_DB.prepare(
      `INSERT INTO outbox (id, subscriber_id, kind, next_attempt_at, created_at)
        SELECT ?1, ?2, 'confirm', ?3, ?3
        WHERE NOT EXISTS (SELECT 1 FROM outbox WHERE subscriber_id = ?2 AND kind = 'confirm'
            AND ((sent_at IS NULL AND attempts < ?4) OR created_at >= ?5))
          AND (SELECT count(*) FROM outbox WHERE subscriber_id = ?2 AND kind = 'confirm') < ?6`,
    ).bind(newId(), id, time, MAX_ATTEMPTS, sqlTime(now - CONFIRM_COOLDOWN_MS), MAX_CONFIRMATIONS),
  ]);
  return (queued?.meta.changes ?? 0) > 0 ? { ...sent, deliver: id } : sent;
}

interface Subscriber {
  id: string;
  target: string;
  components: string;
  confirmedAt: string | null;
}

/** The subscriber a signed link names, or null when the link is not valid. */
async function linked(
  env: Env,
  id: string | null,
  token: string | null,
  purpose: "confirm" | "manage",
): Promise<Subscriber | null> {
  if (!isId(id) || !token || !env.SUBSCRIPTION_SECRET || !(await verify(env.SUBSCRIPTION_SECRET, purpose, id, token)))
    return null;
  return env.STATUS_DB.prepare(
    "SELECT id, target, components, confirmed_at AS confirmedAt FROM subscribers WHERE id = ?1",
  )
    .bind(id)
    .first<Subscriber>();
}

async function params(request: Request): Promise<URLSearchParams> {
  const url = new URL(request.url);
  if (request.method !== "POST") return url.searchParams;
  const form = await fields(request);
  for (const [name, value] of url.searchParams) if (!form.has(name)) form.set(name, value);
  return form;
}

/** Answers every request under /subscribe. */
export async function subscriptions(env: Env, request: Request, now: number): Promise<Answer> {
  const url = new URL(request.url);
  const method = request.method;
  if (!env.SUBSCRIPTION_SECRET) return { page: unavailablePage(), status: 503 };
  const secret = env.SUBSCRIPTION_SECRET;

  switch (`${method} ${url.pathname}`) {
    case "GET /subscribe":
      return {
        page: subscribeForm({ components: await components(env) }),
        status: 200,
      };
    case "POST /subscribe":
      return subscribe(env, request, now);
    case "GET /subscribe/confirm":
    case "POST /subscribe/confirm": {
      const values = await params(request);
      const subscriber = await linked(env, values.get("id"), values.get("token"), "confirm");
      if (!subscriber) return { page: invalidLinkPage(), status: 404 };
      if (method === "GET")
        return { page: confirmPage(subscriber.id, values.get("token") ?? "", subscriber.target), status: 200 };
      await env.STATUS_DB.prepare("UPDATE subscribers SET confirmed_at = coalesce(confirmed_at, ?2) WHERE id = ?1")
        .bind(subscriber.id, sqlTime(now))
        .run();
      return {
        page: confirmedPage((await links({ ...env, SUBSCRIPTION_SECRET: secret }, subscriber.id)).manage),
        status: 200,
      };
    }
    case "GET /subscribe/manage": {
      const values = await params(request);
      const subscriber = await linked(env, values.get("id"), values.get("token"), "manage");
      if (!subscriber) return { page: invalidLinkPage(), status: 404 };
      const names = new Map((await components(env)).map((component) => [component.id, component.name]));
      const followed = JSON.parse(subscriber.components) as string[];
      return {
        page: managePage(subscriber.id, values.get("token") ?? "", subscriber.target, followed, names),
        status: 200,
      };
    }
    // Also RFC 8058 one-click: the link's query plus "List-Unsubscribe=One-Click".
    case "POST /subscribe/manage":
    case "POST /subscribe/unsubscribe": {
      const values = await params(request);
      const subscriber = await linked(env, values.get("id"), values.get("token"), "manage");
      if (!subscriber) return { page: invalidLinkPage(), status: 404 };
      await env.STATUS_DB.prepare("DELETE FROM subscribers WHERE id = ?1").bind(subscriber.id).run();
      if (values.get("List-Unsubscribe") === "One-Click") return new Response("Unsubscribed.", { status: 200 });
      return { page: unsubscribedPage(), status: 200 };
    }
    default:
      return new Response("Not found", { status: 404 });
  }
}
