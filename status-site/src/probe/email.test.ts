import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { EMAIL_INBOX_UNAVAILABLE } from "../domain/status";
import type { EmailSender } from "../env";
import { emailSample, runEmailProbe, SEND_EVERY_MS, type EmailProbeState } from "./email";

const T0 = Date.parse("2026-10-27T23:00:00Z");

/** Waiting moves the fake clock instead of the test. */
const sleep = async (ms: number) => {
  vi.setSystemTime(Date.now() + ms);
};

/** A mail.tm stand-in: one domain, accounts, tokens, and the messages sent
    to them, each visible once its delivery delay has passed. */
function inbox(options: { delayMs?: number; down?: boolean } = {}) {
  const accounts = new Map<string, string>();
  const messages: { id: string; to: string; subject: string; visibleAt: number }[] = [];
  const calls: string[] = [];
  const fetcher = vi.fn<typeof fetch>(async (input, init) => {
    const url = new URL(String(input));
    const method = init?.method ?? "GET";
    calls.push(`${method} ${url.pathname}`);
    if (options.down) return new Response("busy", { status: 503 });
    const body = init?.body ? (JSON.parse(String(init.body)) as { address: string; password: string }) : null;
    const owner = new Headers(init?.headers).get("authorization")?.slice("Bearer ".length);
    switch (`${method} ${url.pathname}`) {
      case "GET /domains":
        return Response.json({ "hydra:member": [{ domain: "inbox.test", isActive: true }] });
      case "POST /accounts":
        accounts.set(body!.address, body!.password);
        return Response.json({ id: `account-${accounts.size}`, address: body!.address }, { status: 201 });
      case "POST /token":
        return accounts.get(body!.address) === body!.password
          ? Response.json({ token: body!.address })
          : Response.json({ code: 401 }, { status: 401 });
      case "GET /messages":
        return Response.json({
          "hydra:member": messages.filter((message) => message.to === owner && message.visibleAt <= Date.now()),
        });
      default:
        if (method === "DELETE" && url.pathname.startsWith("/messages/")) {
          messages.splice(
            messages.findIndex((message) => `/messages/${message.id}` === url.pathname),
            1,
          );
          return new Response(null, { status: 204 });
        }
        return new Response("not found", { status: 404 });
    }
  });
  const sent: Parameters<EmailSender["send"]>[0][] = [];
  const email: EmailSender = {
    send: vi.fn(async (message) => {
      sent.push(message);
      messages.push({
        id: `m${sent.length}`,
        to: message.to,
        subject: message.subject,
        visibleAt: Date.now() + (options.delayMs ?? 4_000),
      });
      return { messageId: `m${sent.length}` };
    }),
  };
  return { fetcher, email, sent, calls, accounts, messages };
}

/** One round at `at`. */
const round = (state: EmailProbeState, box: ReturnType<typeof inbox>, at: number, email = box.email) => {
  vi.setSystemTime(at);
  return runEmailProbe(state, email, at, box.fetcher, sleep);
};

const sampleOf = (state: EmailProbeState, fault: string | null, now: number, streak = 0) =>
  emailSample("endpoint", state, fault, now, streak);

describe("email delivery probe", () => {
  beforeEach(() => {
    vi.useFakeTimers({ now: T0, toFake: ["Date"] });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("creates its own inbox, sends one message and times its arrival on our clock", async () => {
    const box = inbox();
    const first = await round({}, box, T0);
    expect(first.fault).toBeNull();
    expect(box.sent).toHaveLength(1);
    expect(box.sent[0]).toMatchObject({ from: { email: "noreply@terrascale.tech" }, to: first.state.inbox!.address });
    expect(first.state.inbox!.address).toMatch(/^ts-status-[0-9a-z]{26}@inbox\.test$/);
    // Read every 1.5 seconds: seen at 4.5 seconds, then deleted.
    expect(first.state.pending).toBeUndefined();
    expect(first.state.last).toEqual({ ok: true, ms: 4_500, error: null });
    expect(box.messages).toHaveLength(0);
    expect(sampleOf(first.state, null, T0 + 30_000).sample).toMatchObject({
      state: "operational",
      ok: 1,
      total: 1,
      latencyMs: 4_500,
    });

    // Nothing is due until the next interval: no inbox or sender traffic.
    const calls = box.calls.length;
    const idle = await round(first.state, box, T0 + 60_000);
    expect(box.calls).toHaveLength(calls);
    await round(idle.state, box, T0 + SEND_EVERY_MS);
    expect(box.sent).toHaveLength(2);
    expect(box.accounts.size).toBe(1);
  });

  it("degrades a message slower than 30 seconds, even before it arrives", async () => {
    const box = inbox({ delayMs: 45_000 });
    const sent = await round({}, box, T0);
    expect(sent.state.pending).toBeDefined();
    expect(sampleOf(sent.state, null, T0 + 15_000).sample.state).toBe("none");
    const waiting = await round(sent.state, box, T0 + 40_000);
    expect(sampleOf(waiting.state, null, T0 + 40_000).sample).toMatchObject({
      state: "degraded",
      latencyMs: 40_000,
      results: [{ e: "Not delivered in 30 seconds" }],
    });
    const arrived = await round(waiting.state, box, T0 + 60_000);
    expect(arrived.state.last).toEqual({ ok: true, ms: 60_000, error: null });
    expect(sampleOf(arrived.state, null, T0 + 60_000).sample.state).toBe("degraded");
  });

  it("fails a lost message and a refused send, degraded first and down when it persists", async () => {
    const box = inbox({ delayMs: 10 * 60_000 });
    const sent = await round({}, box, T0);
    const lost = await round(sent.state, box, T0 + 120_000);
    expect(lost.state.last).toEqual({ ok: false, ms: null, error: "Not delivered in 2 minutes" });
    const once = sampleOf(lost.state, null, T0 + 120_000);
    expect(once.sample).toMatchObject({ state: "degraded", ok: 0, total: 1 });
    expect(sampleOf(lost.state, null, T0 + 150_000, once.streak).sample.state).toBe("major_outage");

    const refusing: EmailSender = { send: vi.fn(async () => Promise.reject(new Error("rejected"))) };
    const refused = await round({}, inbox(), T0, refusing);
    expect(refused.state.last).toEqual({ ok: false, ms: null, error: "Send failed" });
    expect(refused.state.pending).toBeUndefined();
  });

  it("leaves Email unmeasured when the inbox API fails, and replaces a deleted inbox", async () => {
    const down = inbox({ down: true });
    const failed = await round({}, down, T0);
    expect(failed.fault).toBe(EMAIL_INBOX_UNAVAILABLE);
    expect(down.sent).toHaveLength(0);
    expect(sampleOf(failed.state, failed.fault, T0).sample).toMatchObject({
      state: "none",
      total: 0,
      results: [{ ok: null, e: EMAIL_INBOX_UNAVAILABLE }],
    });

    const box = inbox();
    const forgotten = { inbox: { id: "gone", address: "old@inbox.test", password: "x" } };
    const replaced = await round(forgotten, box, T0);
    expect(replaced.fault).toBeNull();
    expect(replaced.state.inbox!.address).not.toBe("old@inbox.test");
    expect(box.sent[0]!.to).toBe(replaced.state.inbox!.address);
  });
});
