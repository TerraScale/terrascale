// One object per event: Workers Logs indexes its fields. Keep msg constant and
// put variable values in attributes.
export type LogLevel = "debug" | "info" | "warn" | "error";

export interface LogFields {
  component: string;
  request?: Request;
  error?: unknown;
  [field: string]: unknown;
}

const TRACEPARENT = /^[0-9a-f]{2}-(?!0{32})([0-9a-f]{32})-[0-9a-f]{16}-[0-9a-f]{2}$/;

export function logEvent(level: LogLevel, msg: string, { request, error, ...fields }: LogFields): void {
  const traceId = TRACEPARENT.exec(request?.headers.get("traceparent") ?? "")?.[1];
  console[level]({
    level,
    msg,
    service: "ts-status",
    ...fields,
    ...(request && { region: request.cf?.colo }),
    ...(traceId && { trace_id: traceId }),
    ...(error !== undefined && { error: error instanceof Error ? error.message : String(error) }),
  });
}
