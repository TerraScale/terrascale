export type McpLogger = {
  readonly info: (event: string, attributes?: Readonly<Record<string, string | number | boolean>>) => void;
  readonly warn: (event: string, attributes?: Readonly<Record<string, string | number | boolean>>) => void;
  readonly error: (event: string, attributes?: Readonly<Record<string, string | number | boolean>>) => void;
};

export function createStructuredLogger(write: (line: string) => void = console.error): McpLogger {
  return {
    info: (event, attributes) => writeLog(write, "INFO", event, attributes),
    warn: (event, attributes) => writeLog(write, "WARN", event, attributes),
    error: (event, attributes) => writeLog(write, "ERROR", event, attributes),
  };
}

function writeLog(
  write: (line: string) => void,
  severityText: "INFO" | "WARN" | "ERROR",
  event: string,
  attributes: Readonly<Record<string, string | number | boolean>> | undefined,
): void {
  write(JSON.stringify({ timestamp: new Date().toISOString(), severityText, body: event, attributes }));
}
