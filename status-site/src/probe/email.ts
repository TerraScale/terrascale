/* End-to-end email delivery: a message sent through Cloudflare Email
   Sending, as subscription mail is, to a disposable mail.tm inbox the probe
   creates for itself, timed until it arrives. A silent sender failure, which
   the provider's status page cannot show, fails it. */

import { newId } from "../ids";
import { logEvent } from "../log";

import { settle } from "../domain/consensus";
import { EMAIL_INBOX_UNAVAILABLE, type TimelineState } from "../domain/status";
import type { EmailSender } from "../env";
import { SENDER } from "../notify/outbox";
import { canaryJson, CanaryError, CANARY_VANTAGE } from "./canary";
import { USER_AGENT } from "./check";
import type { Sample } from "./round";

/** The inbox API, also the probe endpoint's URL. */
export const INBOX_API = "https://api.mail.tm";

/** One message per this interval: 288 a day, all to the probe's own inbox. */
export const SEND_EVERY_MS = 5 * 60_000;

/** A message slower than this to arrive is degraded. */
export const ARRIVAL_SLOW_MS = 30_000;

/** A message not arrived after this is lost. */
export const ARRIVAL_LOST_MS = 2 * 60_000;

/** After sending, the inbox is read this often, for up to QUICK_WAIT_MS, so
    a prompt arrival is timed on our clock to this resolution. Later rounds
    look again every 30 seconds. */
const POLL_MS = 1_500;
const QUICK_WAIT_MS = 15_000;

const pause = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/** What the probe remembers between rounds. */
export interface EmailProbeState {
  /** The probe's own inbox; its password never leaves the coordinator. */
  inbox?: { id: string; address: string; password: string };
  /** When the last message was sent. */
  sentAt?: number;
  /** The message on its way. */
  pending?: { marker: string; sentAt: number };
  /** The last finished delivery: its arrival time, or why it failed. */
  last?: { ok: boolean; ms: number | null; error: string | null };
}

/** The inbox API's own failure: no evidence about our email. */
class InboxError extends CanaryError {}

interface Message {
  id: string;
  subject?: string;
}

const members = <T>(body: unknown): T[] =>
  Array.isArray(body) ? (body as T[]) : (((body as { "hydra:member"?: T[] })["hydra:member"] ?? []) as T[]);

async function inboxCall(
  path: string,
  init: RequestInit,
  fetcher: typeof fetch,
  statuses?: readonly number[],
): Promise<unknown> {
  try {
    return await canaryJson(
      INBOX_API + path,
      {
        ...init,
        headers: { "user-agent": USER_AGENT, accept: "application/json", ...(init.headers as Record<string, string>) },
        cache: "no-store",
      },
      fetcher,
      statuses,
    );
  } catch (error) {
    if (error instanceof CanaryError) throw new InboxError(`Inbox ${error.message}`, error.status);
    throw error;
  }
}

const json = (body: unknown): RequestInit => ({
  method: "POST",
  headers: { "content-type": "application/json" },
  body: JSON.stringify(body),
});

/** The probe's inbox, created on first use or after the inbox API forgot it. */
async function openInbox(
  state: EmailProbeState,
  fetcher: typeof fetch,
): Promise<{ inbox: NonNullable<EmailProbeState["inbox"]>; token: string }> {
  if (state.inbox) {
    try {
      const { token } = (await inboxCall(
        "/token",
        json({ address: state.inbox.address, password: state.inbox.password }),
        fetcher,
        [200],
      )) as { token?: unknown };
      if (typeof token === "string") return { inbox: state.inbox, token };
    } catch (error) {
      // A deleted inbox answers 401: make a new one. Anything else is the API's fault.
      if (!(error instanceof InboxError && error.status === 401)) throw error;
    }
  }
  const domain = members<{ domain?: unknown; isActive?: unknown }>(await inboxCall("/domains", {}, fetcher)).find(
    (entry) => typeof entry.domain === "string" && entry.isActive !== false,
  )?.domain as string | undefined;
  if (!domain) throw new InboxError("Inbox has no domain");
  const address = `ts-status-${newId().toLowerCase()}@${domain}`;
  const password = crypto.randomUUID();
  const account = (await inboxCall("/accounts", json({ address, password }), fetcher, [201])) as { id?: unknown };
  if (typeof account.id !== "string") throw new InboxError("Inbox invalid response");
  const inbox = { id: account.id, address, password };
  const { token } = (await inboxCall("/token", json({ address, password }), fetcher, [200])) as { token?: unknown };
  if (typeof token !== "string") throw new InboxError("Inbox invalid response");
  return { inbox, token };
}

/** Advances the probe one round: checks the message on its way, then sends
    the next when one is due. Returns the state for the next round. */
export async function runEmailProbe(
  state: EmailProbeState,
  email: EmailSender,
  now: number,
  fetcher: typeof fetch = fetch,
  sleep: (ms: number) => Promise<void> = pause,
): Promise<{ state: EmailProbeState; fault: string | null }> {
  const next: EmailProbeState = { ...state };
  try {
    const due = next.sentAt === undefined || now - next.sentAt >= SEND_EVERY_MS;
    if (!next.pending && !due) return { state: next, fault: null };
    const { inbox, token } = await openInbox(next, fetcher);
    next.inbox = inbox;
    const auth = { headers: { authorization: `Bearer ${token}` } };
    // Arrival is timed on our own clock: the inbox's timestamps are neither
    // precise nor in step with it.
    const arrived = async (): Promise<boolean> => {
      const { marker, sentAt } = next.pending!;
      const found = members<Message>(await inboxCall("/messages", auth, fetcher)).find((message) =>
        message.subject?.includes(marker),
      );
      if (!found) return false;
      next.last = { ok: true, ms: Date.now() - sentAt, error: null };
      delete next.pending;
      await inboxCall(`/messages/${found.id}`, { ...auth, method: "DELETE" }, fetcher, [204]).catch(() => {});
      return true;
    };
    if (next.pending && !(await arrived()) && Date.now() - next.pending.sentAt >= ARRIVAL_LOST_MS) {
      next.last = { ok: false, ms: null, error: "Not delivered in 2 minutes" };
      delete next.pending;
    }
    if (!next.pending && due) {
      const marker = newId();
      next.sentAt = now;
      try {
        const sentAt = Date.now();
        await email.send({
          from: SENDER,
          to: inbox.address,
          subject: `TerraScale status delivery probe ${marker}`,
          text: `Delivery probe ${marker} from status.terrascale.tech.`,
          html: `<p>Delivery probe ${marker} from status.terrascale.tech.</p>`,
          headers: { "X-TerraScale-Probe": "email-delivery" },
        });
        next.pending = { marker, sentAt };
      } catch (error) {
        // The silent failure this probe exists for: the provider refused or lost the send.
        logEvent("error", "email probe send failed", { component: "email", error });
        next.last = { ok: false, ms: null, error: "Send failed" };
      }
      while (next.pending && Date.now() - next.pending.sentAt < QUICK_WAIT_MS) {
        await sleep(POLL_MS);
        if (await arrived()) break;
      }
    }
    return { state: next, fault: null };
  } catch (error) {
    if (!(error instanceof InboxError)) throw error;
    logEvent("error", "email probe inbox unavailable", { component: "email", error: error.message });
    return { state: next, fault: EMAIL_INBOX_UNAVAILABLE };
  }
}

/** The round's sample: the last finished delivery, or the one on its way once
    it is already slower than ARRIVAL_SLOW_MS; one failure shows as degraded,
    two rounds as an outage. */
export function emailSample(
  endpointId: string,
  state: EmailProbeState,
  fault: string | null,
  now: number,
  streak: number,
): { sample: Sample; streak: number } {
  let round: TimelineState;
  let ok: 0 | 1 | null;
  let ms: number | null = null;
  let error: string | null = null;
  const waiting = state.pending && now - state.pending.sentAt > ARRIVAL_SLOW_MS ? now - state.pending.sentAt : null;
  if (fault) {
    round = "none";
    ok = null;
    error = fault;
  } else if (!state.last && waiting === null) {
    round = "none";
    ok = null;
  } else if (state.last && !state.last.ok && waiting === null) {
    round = "major_outage";
    ok = 0;
    error = state.last.error;
  } else if (waiting !== null) {
    round = "degraded";
    ok = 1;
    ms = waiting;
    error = "Not delivered in 30 seconds";
  } else {
    ms = state.last!.ms;
    round = (ms ?? 0) > ARRIVAL_SLOW_MS ? "degraded" : "operational";
    ok = 1;
  }
  const settled = settle(round, streak);
  return {
    sample: {
      endpointId,
      state: settled.state,
      ok: ok === 1 ? 1 : 0,
      total: ok === null ? 0 : 1,
      latencyMs: ms,
      results: [{ v: CANARY_VANTAGE, ok, ms, e: error }],
      values: null,
    },
    streak: settled.streak,
  };
}
