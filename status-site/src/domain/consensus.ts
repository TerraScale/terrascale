/* Consensus across vantage points. Every round, each vantage probes every
   endpoint; the round's verdict needs at least two answering vantages, and
   an endpoint is down only when most of them fail. A vantage that does not
   answer at all is unknown, never a failure. */

import { SLOW_MS, type TimelineState } from "./status";

/** One vantage's probe of one endpoint. */
export interface VantageResult {
  vantage: string;
  ok: boolean;
  /** Null when the time measured is not the endpoint's own. */
  latencyMs: number | null;
  error: string | null;
}

export interface Consensus {
  /** The round's state before hysteresis. */
  state: TimelineState;
  /** Vantages that passed, out of those that answered. */
  ok: number;
  total: number;
  /** Median latency of the passing vantages. */
  latencyMs: number | null;
}

/** Fewer answering vantages than this cannot judge an endpoint. */
export const MIN_VANTAGES = 2;

/** Consecutive down rounds before an endpoint shows as down. */
export const CONFIRM_DOWN = 2;

export function median(values: readonly number[]): number | null {
  if (values.length === 0) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 1
    ? (sorted[middle] ?? null)
    : Math.round(((sorted[middle - 1] ?? 0) + (sorted[middle] ?? 0)) / 2);
}

/** Whether a round's counts mean the endpoint was down. */
export const isDown = (ok: number, total: number): boolean => total >= MIN_VANTAGES && (total - ok) * 2 > total;

export function consensus(results: readonly VantageResult[]): Consensus {
  const passed = results.filter((result) => result.ok);
  const ok = passed.length;
  const total = results.length;
  const latencyMs = median(passed.flatMap((result) => (result.latencyMs === null ? [] : [result.latencyMs])));
  let state: TimelineState;
  if (total < MIN_VANTAGES) state = "none";
  else if (isDown(ok, total)) state = "major_outage";
  else if (ok < total || (latencyMs ?? 0) > SLOW_MS) state = "degraded";
  else state = "operational";
  return { state, ok, total, latencyMs };
}

/** Applies hysteresis: the first down round shows as degraded, so a single
    blip never reports an outage. Returns the state and the new down streak.
    An unknown round carries no evidence either way and keeps the streak, so
    gaps between down rounds cannot mask a sustained outage. */
export function settle(round: TimelineState, streak: number): { state: TimelineState; streak: number } {
  if (round === "none") return { state: round, streak };
  if (round !== "major_outage") return { state: round, streak: 0 };
  const next = streak + 1;
  return { state: next >= CONFIRM_DOWN ? "major_outage" : "degraded", streak: next };
}
