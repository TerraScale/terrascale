import { createHash, randomBytes } from "node:crypto";

import type * as Models from "../../models.js";
import type { ProjectId, QueueId } from "../../ids.js";
import {
  jsonResponse,
  matchLocalRoute,
  objectBody,
  problemResponse,
  type LocalHandler,
  type LocalRequestContext,
} from "../context.js";

type Store = {
  get(namespace: string, key: string): unknown | PromiseLike<unknown>;
  list(namespace: string): unknown | PromiseLike<unknown>;
  put(namespace: string, key: string, value: unknown): unknown | PromiseLike<unknown>;
  delete(namespace: string, key: string): unknown | PromiseLike<unknown>;
  nextUlid(): string | PromiseLike<string>;
};

type QueueRecord = Models.ProjectQueue & { readonly description?: string };
type QueueMessage = {
  readonly projectId: string;
  readonly queueId: string;
  readonly messageId: string;
  readonly data: Models.JsonValue;
  readonly contentType: string;
  readonly headers: Readonly<Record<string, string>>;
  readonly sequence: number;
  readonly createdAt: string;
  readonly availableAt: string;
  readonly expiresAt?: string;
  readonly deliveryAttempt: number;
  readonly state: "available" | "leased" | "completed" | "dead-letter";
};
type QueueLease = {
  readonly receipt: string;
  readonly projectId: string;
  readonly queueId: string;
  readonly messageId: string;
  readonly consumerName: string;
  readonly consumerSequence: number;
  readonly lockedUntil: string;
};
type PublishResult = Models.ProjectQueuePublishResult & {
  readonly dataFingerprint?: string;
  readonly projectId?: string;
  readonly queueId?: string;
  readonly idempotencyKey?: string;
};
type ConsumerRecord = {
  readonly projectId: string;
  readonly queueId: string;
  readonly consumerName: string;
  readonly sequence: number;
};
type RouteParams = { readonly projectId: string; readonly queueId: string };

const QUEUES = "queues";
const MESSAGES = "queue-messages";
const LEASES = "queue-leases";
const PUBLISHES = "queue-publishes";
const CONSUMERS = "queue-consumers";
const REGION = "GRU" as const;
const STORE_SCOPE = "local";

export const handleLocalQueues: LocalHandler = async (context) => {
  const raw = context.store;
  const store: Store = {
    get: (namespace, key) => raw.get(namespace, STORE_SCOPE, key),
    list: (namespace) => raw.list(namespace, STORE_SCOPE),
    put: (namespace, key, value) => raw.put(namespace, STORE_SCOPE, key, value),
    delete: (namespace, key) => raw.delete(namespace, STORE_SCOPE, key),
    nextUlid: () => raw.nextUlid(),
  };
  const body = objectBody(context);
  let match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/queues");
  if (match !== undefined) {
    match = match as RouteParams;
    const queues = (await records<QueueRecord>(store, QUEUES)).filter(
      (q) => q.projectId === match!.projectId,
    );
    const messages = await records<QueueMessage>(store, MESSAGES);
    const leases = await records<QueueLease>(store, LEASES);
    return jsonResponse(
      queues.map((q) =>
        queueWithCounters(
          q,
          messages.filter((m) => m.projectId === q.projectId && m.queueId === q.queueId),
          leases.filter((l) => l.projectId === q.projectId && l.queueId === q.queueId),
        ),
      ),
    );
  }

  match = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/queues");
  if (match !== undefined) {
    match = match as RouteParams;
    const name = text(body.name);
    if (name === undefined) return problemResponse(400, "invalid_request", "name is required.");
    const existing = (await records<QueueRecord>(store, QUEUES)).find(
      (q) => q.projectId === match!.projectId && q.name === name,
    );
    if (existing !== undefined)
      return problemResponse(409, "queue_already_exists", "A queue with that name already exists.");
    const queueId = (await store.nextUlid()) as QueueId;
    const queue: QueueRecord = {
      queueId,
      projectId: match.projectId as ProjectId,
      name,
      kind: body.kind === "topic" ? "Topic" : "Queue",
      messages: 0,
      storageBytes: 0,
      consumerCount: 0,
      replicas: 1,
      fifo: body.fifo === true,
      partitionCount: positiveInt(body.partitionCount, 1),
      retentionSeconds:
        positiveInt(body.retentionSeconds, 0) > 0 ? positiveInt(body.retentionSeconds, 0) : null,
      createdAt: context.now,
    };
    await store.put(QUEUES, queueKey(required(match.projectId), queueId), queue);
    return jsonResponse(queue, 201);
  }

  match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/queues/{queueId}:peek");
  if (match !== undefined) {
    match = match as RouteParams;
    const queue = await getQueue(store, required(match.projectId), required(match.queueId));
    if (queue === undefined) return notFound("queue");
    const messages = await availableMessages(store, match, context.now);
    return jsonResponse(
      messages.slice(0, positiveInt(context.url.searchParams.get("maxMessages"), 10)).map(toPeek),
    );
  }

  match = matchLocalRoute(context, "GET", "/api/v1/projects/{projectId}/queues/{queueId}");
  if (match !== undefined) {
    match = match as RouteParams;
    const queue = await getQueue(store, required(match.projectId), required(match.queueId));
    return queue === undefined ? notFound("queue") : jsonResponse(await currentQueue(store, queue));
  }
  match = matchLocalRoute(context, "DELETE", "/api/v1/projects/{projectId}/queues/{queueId}");
  if (match !== undefined) {
    match = match as RouteParams;
    const queue = await getQueue(store, required(match.projectId), required(match.queueId));
    if (queue === undefined) return notFound("queue");
    const messages = await records<QueueMessage>(store, MESSAGES);
    const leases = await records<QueueLease>(store, LEASES);
    const publishes = await records<PublishResult>(store, PUBLISHES);
    const consumers = await records<ConsumerRecord>(store, CONSUMERS);
    for (const item of messages.filter((m) => sameQueue(m, match!)))
      await store.delete(MESSAGES, messageKey(item.projectId, item.queueId, item.messageId));
    for (const item of leases.filter((l) => sameQueue(l, match!)))
      await store.delete(LEASES, item.receipt);
    for (const item of publishes.filter(
      (p) => p.projectId === match!.projectId && p.queueId === match!.queueId,
    ))
      await store.delete(
        PUBLISHES,
        `${item.projectId}:${item.queueId}:${item.idempotencyKey ?? ""}`,
      );
    for (const item of consumers.filter((c) => sameQueue(c, match!)))
      await store.delete(CONSUMERS, `${item.projectId}:${item.queueId}:${item.consumerName}`);
    await store.delete(QUEUES, queueKey(required(match.projectId), required(match.queueId)));
    return jsonResponse(true);
  }

  match = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/queues/{queueId}:purge");
  if (match !== undefined) {
    match = match as RouteParams;
    const queue = await getQueue(store, required(match.projectId), required(match.queueId));
    if (queue === undefined) return notFound("queue");
    const messages = await records<QueueMessage>(store, MESSAGES);
    const leases = await records<QueueLease>(store, LEASES);
    const affected = messages.filter((m) => sameQueue(m, match!));
    for (const item of affected)
      await store.delete(MESSAGES, messageKey(item.projectId, item.queueId, item.messageId));
    for (const item of leases.filter((l) => sameQueue(l, match!)))
      await store.delete(LEASES, item.receipt);
    return jsonResponse({ queueName: queue.name, purgedMessages: affected.length });
  }

  match = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/queues/{queueId}:publish");
  if (match !== undefined) {
    match = match as RouteParams;
    const queue = await getQueue(store, required(match.projectId), required(match.queueId));
    if (queue === undefined) return notFound("queue");
    const data = body.data as Models.JsonValue;
    if (data === undefined) return problemResponse(400, "invalid_request", "data is required.");
    const idempotencyKey = text(body.idempotencyKey);
    const publishKey =
      idempotencyKey === undefined
        ? undefined
        : `${required(match.projectId)}:${required(match.queueId)}:${idempotencyKey}`;
    const dataFingerprint = fingerprint(body);
    if (publishKey !== undefined) {
      const old = await get<PublishResult>(store, PUBLISHES, publishKey);
      if (old !== undefined) {
        if (old.dataFingerprint !== undefined && old.dataFingerprint !== dataFingerprint)
          return problemResponse(
            409,
            "idempotency_key_reused",
            "The idempotency key was already used with a different message.",
          );
        const {
          dataFingerprint: _fingerprint,
          projectId: _projectId,
          queueId: _queueId,
          idempotencyKey: _idempotencyKey,
          ...replayed
        } = old;
        return jsonResponse({ ...replayed, duplicate: true });
      }
    }
    const messages = await records<QueueMessage>(store, MESSAGES);
    const sequence =
      Math.max(0, ...messages.filter((m) => sameQueue(m, match!)).map((m) => m.sequence)) + 1;
    const messageId = await store.nextUlid();
    const delay = positiveInt(body.delaySeconds, 0);
    const ttl = positiveInt(body.timeToLiveSeconds, 0);
    const message: QueueMessage = {
      projectId: required(match.projectId),
      queueId: required(match.queueId),
      messageId,
      data,
      contentType: "application/json",
      headers: {},
      sequence,
      createdAt: context.now,
      availableAt: new Date(Date.parse(context.now) + delay * 1000).toISOString(),
      ...(ttl > 0
        ? { expiresAt: new Date(Date.parse(context.now) + ttl * 1000).toISOString() }
        : {}),
      deliveryAttempt: 0,
      state: "available",
    };
    await store.put(
      MESSAGES,
      messageKey(required(match.projectId), required(match.queueId), messageId),
      message,
    );
    const result: PublishResult = {
      messageId,
      sequence,
      publishedAt: context.now,
      duplicate: false,
      dataFingerprint,
      projectId: required(match.projectId),
      queueId: required(match.queueId),
      ...(idempotencyKey === undefined ? {} : { idempotencyKey }),
    };
    if (publishKey !== undefined) await store.put(PUBLISHES, publishKey, result);
    const {
      dataFingerprint: _fingerprint,
      projectId: _projectId,
      queueId: _queueId,
      idempotencyKey: _idempotencyKey,
      ...publicResult
    } = result;
    return jsonResponse(publicResult);
  }

  match = matchLocalRoute(context, "POST", "/api/v1/projects/{projectId}/queues/{queueId}:receive");
  if (match !== undefined) {
    match = match as RouteParams;
    const queue = await getQueue(store, required(match.projectId), required(match.queueId));
    if (queue === undefined) return notFound("queue");
    const consumerName = text(body.consumerName);
    if (consumerName === undefined)
      return problemResponse(400, "invalid_request", "consumerName is required.");
    const max = Math.min(100, positiveInt(body.maxMessages, 10));
    const lockSeconds = Math.min(86_400, Math.max(1, positiveInt(body.lockSeconds, 30)));
    const messages = await availableMessages(store, match, context.now);
    const consumerKey = `${required(match.projectId)}:${required(match.queueId)}:${consumerName}`;
    const consumerRecord = await get<number | ConsumerRecord>(store, CONSUMERS, consumerKey);
    const consumerSequence =
      typeof consumerRecord === "number" ? consumerRecord : (consumerRecord?.sequence ?? 0);
    const received: Models.ProjectQueueReceivedMessage[] = [];
    for (const message of messages.slice(0, max)) {
      const receipt = opaqueReceipt();
      const lease: QueueLease = {
        receipt,
        projectId: required(match.projectId),
        queueId: required(match.queueId),
        messageId: message.messageId,
        consumerName,
        consumerSequence: consumerSequence + received.length + 1,
        lockedUntil: new Date(Date.parse(context.now) + lockSeconds * 1000).toISOString(),
      };
      await store.put(LEASES, receipt, lease);
      await store.put(MESSAGES, messageKey(message.projectId, message.queueId, message.messageId), {
        ...message,
        state: "leased",
        deliveryAttempt: message.deliveryAttempt + 1,
      });
      received.push({
        receipt,
        region: REGION,
        messageId: message.messageId,
        data: message.data,
        contentType: message.contentType,
        headers: message.headers,
        streamSequence: message.sequence,
        consumerSequence: lease.consumerSequence,
        deliveryAttempt: message.deliveryAttempt + 1,
        receivedAt: context.now,
        lockedUntil: lease.lockedUntil,
        deliveryGuarantee: "at-least-once",
      });
    }
    if (received.length > 0)
      await store.put(CONSUMERS, consumerKey, {
        projectId: required(match.projectId),
        queueId: required(match.queueId),
        consumerName,
        sequence: consumerSequence + received.length,
      } satisfies ConsumerRecord);
    return jsonResponse(received);
  }

  for (const action of ["complete", "abandon", "dead-letter", "renew-lock"] as const) {
    match = matchLocalRoute(
      context,
      "POST",
      `/api/v1/projects/{projectId}/queues/{queueId}:${action}`,
    );
    if (match !== undefined) return queueAction(store, context, match as RouteParams, action, body);
  }
  return undefined;
};

async function queueAction(
  store: Store,
  context: LocalRequestContext,
  scope: Readonly<Record<string, string>>,
  action: "complete" | "abandon" | "dead-letter" | "renew-lock",
  body: Record<string, unknown>,
): Promise<Response> {
  const receipt = text(body.receipt);
  if (receipt === undefined) return problemResponse(400, "invalid_request", "receipt is required.");
  const lease = await get<QueueLease>(store, LEASES, receipt);
  if (lease === undefined || lease.projectId !== scope.projectId || lease.queueId !== scope.queueId)
    return problemResponse(404, "receipt_not_found", "The queue receipt was not found.");
  const message = await get<QueueMessage>(
    store,
    MESSAGES,
    messageKey(lease.projectId, lease.queueId, lease.messageId),
  );
  if (message === undefined)
    return problemResponse(404, "message_not_found", "The queue message was not found.");
  const expired = Date.parse(lease.lockedUntil) <= Date.parse(context.now);
  if (expired) {
    await store.delete(LEASES, receipt);
    await store.put(MESSAGES, messageKey(message.projectId, message.queueId, message.messageId), {
      ...message,
      state: "available",
    });
    if (action === "renew-lock")
      return problemResponse(409, "lock_expired", "The queue receipt lock has expired.");
    return jsonResponse(actionResult(message.messageId, action, false), 202);
  }
  if (action === "renew-lock") {
    const lockedUntil = new Date(Date.parse(context.now) + 30_000).toISOString();
    await store.put(LEASES, receipt, { ...lease, lockedUntil });
    return jsonResponse(
      {
        receipt,
        region: REGION,
        messageId: message.messageId,
        lockedUntil,
        deliveryGuarantee: "at-least-once",
      } satisfies Models.ProjectQueueRenewLockResult,
      202,
    );
  }
  await store.delete(LEASES, receipt);
  const state =
    action === "complete" ? "completed" : action === "dead-letter" ? "dead-letter" : "available";
  await store.put(MESSAGES, messageKey(message.projectId, message.queueId, message.messageId), {
    ...message,
    state,
    ...(action === "abandon" && positiveInt(body.delaySeconds, 0) > 0
      ? {
          availableAt: new Date(
            Date.parse(context.now) + positiveInt(body.delaySeconds, 0) * 1000,
          ).toISOString(),
        }
      : {}),
  });
  return jsonResponse(actionResult(message.messageId, action, true), 202);
}

function actionResult(
  messageId: string,
  operation: "complete" | "abandon" | "dead-letter",
  accepted: boolean,
): Models.ProjectQueueReceiptActionResult {
  return {
    messageId,
    operation,
    accepted,
    safeToRetry: !accepted,
    region: REGION,
    deliveryGuarantee: "at-least-once",
  };
}
async function availableMessages(
  store: Store,
  scope: Readonly<Record<string, string>>,
  now: string,
): Promise<QueueMessage[]> {
  const leases = await records<QueueLease>(store, LEASES);
  for (const lease of leases.filter(
    (item) => sameQueue(item, scope) && Date.parse(item.lockedUntil) <= Date.parse(now),
  )) {
    await store.delete(LEASES, lease.receipt);
    const leased = await get<QueueMessage>(
      store,
      MESSAGES,
      messageKey(lease.projectId, lease.queueId, lease.messageId),
    );
    if (leased?.state === "leased")
      await store.put(MESSAGES, messageKey(leased.projectId, leased.queueId, leased.messageId), {
        ...leased,
        state: "available",
      });
  }
  const messages = (await records<QueueMessage>(store, MESSAGES)).filter((m) =>
    sameQueue(m, scope),
  );
  const result: QueueMessage[] = [];
  for (const message of messages) {
    if (message.expiresAt !== undefined && Date.parse(message.expiresAt) <= Date.parse(now)) {
      await store.delete(
        MESSAGES,
        messageKey(message.projectId, message.queueId, message.messageId),
      );
      continue;
    }
    result.push(message);
  }
  return result
    .filter((m) => m.state === "available" && Date.parse(m.availableAt) <= Date.parse(now))
    .sort((a, b) => a.sequence - b.sequence);
}
function toPeek(message: QueueMessage): Models.ProjectQueuePeekMessage {
  return {
    messageId: message.messageId,
    data: message.data,
    contentType: message.contentType,
    headers: message.headers,
    sequence: message.sequence,
    createdAt: message.createdAt,
  };
}
async function currentQueue(store: Store, queue: QueueRecord): Promise<Models.ProjectQueue> {
  const messages = (await records<QueueMessage>(store, MESSAGES)).filter(
    (m) => m.projectId === queue.projectId && m.queueId === queue.queueId,
  );
  const leases = (await records<QueueLease>(store, LEASES)).filter(
    (l) => l.projectId === queue.projectId && l.queueId === queue.queueId,
  );
  return queueWithCounters(queue, messages, leases);
}
function queueWithCounters(
  queue: QueueRecord,
  messages: readonly QueueMessage[],
  leases: readonly QueueLease[] = [],
): Models.ProjectQueue {
  const live = messages.filter((m) => m.state === "available" || m.state === "leased");
  return {
    ...queue,
    messages: live.length,
    storageBytes: live.reduce(
      (n, m) => n + new TextEncoder().encode(JSON.stringify(m.data)).byteLength,
      0,
    ),
    consumerCount: new Set(leases.map((l) => l.consumerName)).size,
    retentionSeconds: queue.retentionSeconds ?? null,
  };
}
async function getQueue(
  store: Store,
  projectId: string,
  queueId: string,
): Promise<QueueRecord | undefined> {
  return get<QueueRecord>(store, QUEUES, queueKey(projectId, queueId));
}
async function records<T>(store: Store, namespace: string): Promise<T[]> {
  const value = await store.list(namespace);
  return Array.isArray(value)
    ? (value as T[])
    : value !== null && typeof value === "object"
      ? (Object.values(value) as T[])
      : [];
}
async function get<T>(store: Store, namespace: string, key: string): Promise<T | undefined> {
  return (await store.get(namespace, key)) as T | undefined;
}
function queueKey(projectId: string, queueId: string): string {
  return `${projectId}:${queueId}`;
}
function messageKey(projectId: string, queueId: string, messageId: string): string {
  return `${projectId}:${queueId}:${messageId}`;
}
function sameQueue(
  value: { readonly projectId: string; readonly queueId: string },
  scope: Readonly<Record<string, string>>,
): boolean {
  return value.projectId === scope.projectId && value.queueId === scope.queueId;
}
function opaqueReceipt(): string {
  return randomBytes(32).toString("base64url");
}
function text(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() !== "" ? value.trim() : undefined;
}
function positiveInt(value: unknown, fallback: number): number {
  const number =
    typeof value === "string" && value.trim() !== ""
      ? Number(value)
      : typeof value === "number"
        ? value
        : fallback;
  return Number.isFinite(number) && number > 0 ? Math.floor(number) : fallback;
}
function notFound(resource: string): Response {
  return problemResponse(404, "not_found", `${resource} was not found.`);
}
function required(value: string | undefined): string {
  return value ?? "";
}
function fingerprint(body: Record<string, unknown>): string {
  return createHash("sha256")
    .update(
      JSON.stringify({
        data: body.data,
        partitionKey: body.partitionKey,
        delaySeconds: body.delaySeconds,
        timeToLiveSeconds: body.timeToLiveSeconds,
      }),
      "utf8",
    )
    .digest("hex");
}
