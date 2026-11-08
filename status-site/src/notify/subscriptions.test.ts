import { isId, newId } from "../ids";
import { afterEach, describe, expect, it, vi } from "vitest";
import app from "../app";
import type { Env } from "../env";
import { testD1, type TestD1 } from "../testing/d1";
import { runOutbox } from "./outbox";
import { sign } from "./tokens";

// Subscribing stamps rows with the real clock; the hold test uses fixed times.
const BASE = Date.parse("2026-09-26T10:00:00Z");
const DISCORD = "https://discord.com/api/webhooks/123/abc";

function setup(extra: Partial<Env> = {}) {
  const emails: { from: { email: string }; to: string; subject: string }[] = [];
  const env = {
    STATUS_DB: testD1(),
    SUBSCRIPTION_SECRET: "test-secret",
    PUBLIC_ORIGIN: "https://status.test",
    EMAIL: {
      send: async (message: (typeof emails)[number]) => {
        emails.push(message);
        return { messageId: newId() };
      },
    },
    ...extra,
  } as unknown as Env & { STATUS_DB: TestD1 };
  const posted: { url: string; body: Record<string, unknown>; headers: Headers }[] = [];
  let failing = false;
  const fetcher = (async (url: string, init: RequestInit) => {
    posted.push({ url, body: JSON.parse(String(init.body)), headers: new Headers(init.headers) });
    return new Response(null, { status: failing ? 500 : 204 });
  }) as typeof fetch;
  // A subscription sends its confirmation in the background; the form helper
  // waits for it, and webhooks it posts land in `posted`.
  vi.stubGlobal("fetch", fetcher);
  const background: Promise<unknown>[] = [];
  const ctx = {
    waitUntil: (promise: Promise<unknown>) => background.push(promise),
    passThroughOnException: () => {},
  } as unknown as ExecutionContext;
  const form = async (path: string, fields: Record<string, string | string[]>) => {
    const body = new URLSearchParams();
    for (const [name, value] of Object.entries(fields)) for (const item of [value].flat()) body.append(name, item);
    const response = await app.fetch(
      new Request(`https://status.test${path}`, {
        method: "POST",
        headers: { "content-type": "application/x-www-form-urlencoded", "cf-connecting-ip": "203.0.113.9" },
        body,
      }) as Request<unknown, IncomingRequestCfProperties>,
      env,
      ctx,
    );
    await Promise.all(background.splice(0));
    return response;
  };
  const get = (path: string) =>
    app.fetch(new Request(`https://status.test${path}`) as Request<unknown, IncomingRequestCfProperties>, env, ctx);
  const outbox = (at: number) => runOutbox(env, at, { fetcher });
  return {
    env,
    sql: env.STATUS_DB.sqlite,
    posted,
    emails,
    form,
    get,
    outbox,
    fail: (value: boolean) => (failing = value),
  };
}

/** Confirms a subscriber through the link its confirmation carried. */
async function confirm(t: ReturnType<typeof setup>, content: string) {
  const link = new URL(/https:\/\/status\.test\/subscribe\/confirm\?\S+/.exec(content)?.[0] ?? "");
  const page = await (await t.get(`${link.pathname}${link.search}`)).text();
  expect(page).toContain("Confirm subscription");
  const done = await t.form("/subscribe/confirm", {
    id: link.searchParams.get("id") ?? "",
    token: link.searchParams.get("token") ?? "",
  });
  expect(await done.text()).toContain("You are subscribed");
}

describe("subscriptions", () => {
  it("bounds fan-out queries, resumes the backlog and never duplicates ULID messages", async () => {
    const t = setup();
    const incident = newId();
    const update = newId();
    t.sql
      .prepare("INSERT INTO incidents (id, title, status, impact) VALUES (?, 'Outage', 'investigating', 'minor')")
      .run(incident);
    t.sql
      .prepare(
        "INSERT INTO incident_updates (id, incident_id, status, body) VALUES (?, ?, 'investigating', 'Looking.')",
      )
      .run(update, incident);
    for (let i = 0; i < 1_005; i++)
      t.sql
        .prepare(
          "INSERT INTO subscribers (id, channel, target, flavor, confirmed_at) VALUES (?, 'webhook', ?, 'generic', datetime('now', '-1 minute'))",
        )
        .run(newId(), `https://example.com/hook/${i}`);
    const queries = vi.spyOn(t.env.STATUS_DB, "prepare");
    await t.outbox(Date.now());
    const ids = t.sql.prepare("SELECT id FROM outbox ORDER BY id").all();
    expect(ids).toHaveLength(1_000);
    expect(ids.every((row) => isId(row.id))).toBe(true);
    expect(queries.mock.calls.length).toBeLessThan(30);
    expect(t.posted).toHaveLength(20);
    await t.outbox(Date.now());
    const all = t.sql.prepare("SELECT id FROM outbox ORDER BY id").all();
    expect(all).toHaveLength(1_005);
    expect(all.slice(0, 1_000)).toEqual(ids);
    expect(all.every((row) => isId(row.id))).toBe(true);
    await t.outbox(Date.now());
    expect(t.sql.prepare("SELECT id FROM outbox ORDER BY id").all()).toEqual(all);
  });

  it.each(["subscriber", "update"])(
    "delivers pending mail after an eligible %s is concurrently deleted",
    async (removed) => {
      const t = setup();
      const incident = newId(),
        update = newId(),
        subscriber = newId(),
        survivor = newId();
      t.sql
        .prepare("INSERT INTO incidents (id, title, status, impact) VALUES (?, 'Outage', 'investigating', 'minor')")
        .run(incident);
      t.sql
        .prepare(
          "INSERT INTO incident_updates (id, incident_id, status, body) VALUES (?, ?, 'investigating', 'Looking.')",
        )
        .run(update, incident);
      t.sql
        .prepare(
          "INSERT INTO subscribers (id, channel, target, flavor, confirmed_at) VALUES (?, 'webhook', 'https://example.com/deleted', 'generic', datetime('now', '-1 minute'))",
        )
        .run(subscriber);
      t.sql
        .prepare(
          "INSERT INTO subscribers (id, channel, target, flavor) VALUES (?, 'webhook', 'https://example.com/survivor', 'generic')",
        )
        .run(survivor);
      t.sql.prepare("INSERT INTO outbox (id, subscriber_id, kind) VALUES (?, ?, 'confirm')").run(newId(), survivor);
      const prepare = t.env.STATUS_DB.prepare.bind(t.env.STATUS_DB);
      vi.spyOn(t.env.STATUS_DB, "prepare").mockImplementation((sql) => {
        if (sql.startsWith("INSERT OR IGNORE INTO outbox")) {
          if (removed === "subscriber") t.sql.prepare("DELETE FROM subscribers WHERE id = ?").run(subscriber);
          else t.sql.prepare("DELETE FROM incident_updates WHERE id = ?").run(update);
        }
        return prepare(sql);
      });
      expect(await t.outbox(Date.now())).toEqual({ sent: 1, failed: 0 });
      expect(t.posted.map((entry) => entry.url)).toEqual(["https://example.com/survivor"]);
      expect(t.sql.prepare("SELECT kind, sent_at FROM outbox").all()).toEqual([
        { kind: "confirm", sent_at: expect.any(String) },
      ]);
    },
  );

  afterEach(() => vi.unstubAllGlobals());

  it("confirms a Discord webhook by double opt-in, then delivers updates of the components it follows", async () => {
    const t = setup();
    expect(
      (await t.form("/subscribe", { channel: "webhook", target: DISCORD, components: ["01M4E7G1W82VKFPF5FJ9AVK1CV"] }))
        .status,
    ).toBe(202);
    await t.outbox(Date.now());
    expect(t.posted).toHaveLength(1);
    await confirm(t, String(t.posted[0]?.body.content));

    t.sql.exec(`
      INSERT INTO incidents (id, kind, title, status, impact) VALUES ('00000000000000000000000012', 'incident', 'Dashboard errors', 'investigating', 'major'),
        ('00000000000000000000000013', 'incident', 'Git slow', 'investigating', 'minor');
      INSERT INTO incident_components VALUES ('00000000000000000000000012', '01M4E7G1W82VKFPF5FJ9AVK1CV'), ('00000000000000000000000013', '01M4E7G1W82VKFPF5FJ9AVK1CZ');
      INSERT INTO incident_updates (id, incident_id, status, body, created_at) VALUES
        ('00000000000000000000000014', '00000000000000000000000012', 'investigating', 'Looking.', datetime('now', '+1 minute')),
        ('00000000000000000000000015', '00000000000000000000000013', 'investigating', 'Looking.', datetime('now', '+1 minute'));`);
    await t.outbox(Date.now() + 120_000);
    await t.outbox(Date.now() + 180_000);
    const embeds = t.posted.slice(1).map((entry) => (entry.body.embeds as { title: string }[])[0]?.title);
    expect(embeds).toEqual(["Investigating: Dashboard errors"]);
  });

  it("holds automatic incidents for five minutes and never sends blips", async () => {
    const t = setup();
    t.sql
      .exec(`INSERT INTO subscribers (id, channel, target, flavor, confirmed_at) VALUES ('00000000000000000000000016', 'webhook', '${DISCORD}', 'discord', '2026-09-26 09:00:00');
      INSERT INTO incidents (id, kind, title, status, impact, source, created_at) VALUES
        ('00000000000000000000000017', 'incident', 'Atlas is down', 'investigating', 'critical', 'auto', '2026-09-26 10:00:00');
      INSERT INTO incidents (id, kind, title, status, impact, source, created_at, resolved_at) VALUES
        ('00000000000000000000000018', 'incident', 'Website is down', 'resolved', 'critical', 'auto', '2026-09-26 10:00:00', '2026-09-26 10:03:00');
      INSERT INTO incident_updates (id, incident_id, status, body, created_at) VALUES
        ('00000000000000000000000008', '00000000000000000000000017', 'investigating', 'Down.', '2026-09-26 10:00:00'),
        ('00000000000000000000000009', '00000000000000000000000018', 'investigating', 'Down.', '2026-09-26 10:00:00'),
        ('00000000000000000000000010', '00000000000000000000000018', 'resolved', 'Up.', '2026-09-26 10:03:00');`);
    await t.outbox(BASE + 4 * 60_000);
    expect(t.posted).toHaveLength(0);
    await t.outbox(BASE + 6 * 60_000);
    expect(t.posted.map((entry) => (entry.body.embeds as { title: string }[])[0]?.title)).toEqual([
      "Investigating: Atlas is down",
    ]);
  });

  it("retries failed deliveries with backoff", async () => {
    const t = setup();
    t.fail(true);
    await t.form("/subscribe", { channel: "webhook", target: "https://example.com/hook" });
    expect(t.posted).toHaveLength(1);
    expect(await t.outbox(Date.now() + 30_000)).toEqual({ sent: 0, failed: 0 });
    t.fail(false);
    expect(await t.outbox(Date.now() + 61_000)).toEqual({ sent: 1, failed: 0 });
    const confirmation = t.posted[1];
    const key = String(confirmation?.body.signingKey);
    const { hmacHex } = await import("./webhooks");
    expect(confirmation?.headers.get("x-terrascale-signature")).toBe(
      `sha256=${await hmacHex(key, JSON.stringify(confirmation?.body))}`,
    );
    expect(key).not.toBe("test-secret");
  });

  it("never sends a message twice from overlapping runs", async () => {
    const t = setup();
    t.fail(true);
    await t.form("/subscribe", { channel: "webhook", target: "https://example.com/hook" });
    t.fail(false);
    const later = Date.now() + 61_000;
    const runs = await Promise.all([t.outbox(later), t.outbox(later), t.outbox(later)]);
    expect(runs.reduce((sum, run) => sum + run.sent, 0)).toBe(1);
    expect(t.posted).toHaveLength(2);
  });

  it("sends a target few confirmations, however often it is submitted", async () => {
    const t = setup();
    for (let index = 0; index < 4; index++) {
      expect((await t.form("/subscribe", { channel: "webhook", target: DISCORD })).status).toBe(202);
    }
    expect(t.posted).toHaveLength(1);
    // After the hour's cooldown another goes out, up to three in all.
    for (const expected of [2, 3, 3]) {
      t.sql.exec("UPDATE outbox SET created_at = datetime(created_at, '-2 hours')");
      await t.form("/subscribe", { channel: "webhook", target: DISCORD });
      expect(t.posted).toHaveLength(expected);
    }
  });

  it("unsubscribes with one click and rejects forged links", async () => {
    const t = setup();
    await t.form("/subscribe", { channel: "webhook", target: DISCORD });
    await t.outbox(Date.now());
    await confirm(t, String(t.posted[0]?.body.content));
    const id = String(t.sql.prepare("SELECT id FROM subscribers").get()?.id);
    expect((await t.get(`/subscribe/manage?id=${id}&token=forged`)).status).toBe(404);
    // Well-formed tokens for another purpose or another subscriber fail too.
    expect((await t.get(`/subscribe/manage?id=${id}&token=${await sign("test-secret", "confirm", id)}`)).status).toBe(
      404,
    );
    expect(
      (await t.get(`/subscribe/manage?id=${id}&token=${await sign("test-secret", "manage", "other")}`)).status,
    ).toBe(404);
    const manage = /https:\/\/status\.test\/subscribe\/manage\?\S+/.exec(
      await (await t.form("/subscribe/confirm", {})).text(),
    );
    expect(manage).toBeNull();
    const confirmed = await t.get(`/subscribe`);
    expect(await confirmed.text()).toContain("Subscribe to updates");
    const { links } = await import("./outbox");
    const url = new URL((await links({ ...t.env, SUBSCRIPTION_SECRET: "test-secret" }, id)).manage);
    const response = await t.form(`/subscribe/manage${url.search}`, { "List-Unsubscribe": "One-Click" });
    expect(await response.text()).toBe("Unsubscribed.");
    expect(t.sql.prepare("SELECT count(*) AS n FROM subscribers").get()).toEqual({ n: 0 });
  });

  it("rejects bad targets, ignores bots, limits rates and never reveals subscribers", async () => {
    const limited = { limit: async () => ({ success: false }) } as unknown as RateLimit;
    const t = setup();
    expect(await (await t.form("/subscribe", { channel: "webhook", target: "http://10.0.0.1/x" })).text()).toContain(
      "Enter an HTTPS webhook URL",
    );
    expect((await t.form("/subscribe", { channel: "webhook", target: DISCORD, website: "spam" })).status).toBe(202);
    expect(t.sql.prepare("SELECT count(*) AS n FROM subscribers").get()).toEqual({ n: 0 });
    expect(await (await t.form("/subscribe", { channel: "email", target: "not an address" })).text()).toContain(
      "Enter a valid email address.",
    );
    t.sql.exec(
      `INSERT INTO subscribers (id, channel, target, flavor, confirmed_at) VALUES ('00000000000000000000000016', 'webhook', '${DISCORD}', 'discord', datetime('now'))`,
    );
    expect((await t.form("/subscribe", { channel: "webhook", target: DISCORD })).status).toBe(202);
    const slow = setup({ SUBSCRIBE_LIMITER: limited });
    expect((await slow.form("/subscribe", { channel: "webhook", target: DISCORD })).status).toBe(429);
    const off = setup({ SUBSCRIPTION_SECRET: undefined });
    expect((await off.get("/subscribe")).status).toBe(503);
  });

  it("sends email through the Email Sending binding", async () => {
    const t = setup();
    await t.form("/subscribe", { channel: "email", target: "Ops@Example.com" });
    expect(t.sql.prepare("SELECT target, flavor FROM subscribers").get()).toEqual({
      target: "ops@example.com",
      flavor: "email",
    });
    expect(t.emails).toEqual([
      expect.objectContaining({
        from: { email: "noreply@terrascale.tech", name: "TerraScale Status" },
        to: "ops@example.com",
        subject: "Confirm your TerraScale status subscription",
      }),
    ]);
  });
});
