/* Request body validation for the JSON APIs: small checks that either
   return a clean value or name the field that failed. */

import { isId } from "../ids";

export class Invalid extends Error {
  constructor(
    readonly field: string,
    message: string,
  ) {
    super(`${field}: ${message}`);
  }
}

export type Body = Record<string, unknown>;

export async function readBody(request: Request): Promise<Body> {
  const text = await request.text();
  if (text.length > 32_000) throw new Invalid("body", "must be under 32 KB");
  let value: unknown;
  try {
    value = JSON.parse(text || "{}");
  } catch {
    throw new Invalid("body", "must be JSON");
  }
  if (typeof value !== "object" || value === null || Array.isArray(value))
    throw new Invalid("body", "must be an object");
  return value as Body;
}

export function string(body: Body, field: string, max: number): string {
  const value = body[field];
  if (typeof value !== "string" || value.trim() === "") throw new Invalid(field, "is required");
  if (value.length > max) throw new Invalid(field, `must be at most ${max} characters`);
  return value.trim();
}

export function optionalString(body: Body, field: string, max: number): string | undefined {
  return body[field] === undefined ? undefined : string(body, field, max);
}

export function choice<T extends string>(body: Body, field: string, values: readonly T[]): T {
  const value = body[field];
  if (typeof value !== "string" || !values.includes(value as T)) {
    throw new Invalid(field, `must be one of ${values.join(", ")}`);
  }
  return value as T;
}

export function optionalChoice<T extends string>(body: Body, field: string, values: readonly T[]): T | undefined {
  return body[field] === undefined ? undefined : choice(body, field, values);
}

/** A full ISO 8601 date and time with a zone, such as 2026-10-01T02:00:00Z. */
const ISO_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,3})?)?(Z|[+-]\d{2}:\d{2})$/;

/** Epoch milliseconds from an ISO 8601 time. */
export function instant(body: Body, field: string): number {
  const value = body[field];
  const at = typeof value === "string" && ISO_TIME.test(value) ? Date.parse(value) : Number.NaN;
  if (Number.isNaN(at) || at < 0) throw new Invalid(field, "must be an ISO 8601 time with a zone");
  return at;
}

export function ids(body: Body, field: string): string[] {
  const value = body[field] ?? [];
  if (!Array.isArray(value) || !value.every(isId)) {
    throw new Invalid(field, "must be a list of canonical ULIDs");
  }
  return [...new Set(value as string[])];
}
