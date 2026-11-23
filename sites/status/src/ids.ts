import { monotonicFactory } from "ulid";

/** Canonical Crockford ULID: 128 bits, without prefixes or lowercase aliases. */
export const isId = (value: unknown): value is string =>
  typeof value === "string" && value.length === 26 && /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/.test(value);

/** One generator per isolate, monotonic even when its clock moves backwards. */
export const newId = monotonicFactory();
