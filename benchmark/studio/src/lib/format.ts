// Deterministic value formatting for the studio.
//
// Studio output must not depend on the host locale or ICU data availability:
// every formatter below is plain arithmetic over the input, so a value renders
// identically on the server and in the browser.

/** Formats an integer with comma group separators (`1234567` -> `1,234,567`). */
export function formatInteger(value: number): string {
  if (!Number.isFinite(value)) {
    return "—";
  }
  const rounded = Math.trunc(value);
  const sign = rounded < 0 ? "-" : "";
  const digits = Math.abs(rounded).toString(10);
  const groups: string[] = [];
  for (let end = digits.length; end > 0; end -= 3) {
    groups.unshift(digits.slice(Math.max(0, end - 3), end));
  }
  return `${sign}${groups.join(",")}`;
}

/** Formats a byte count in binary units (`1536` -> `1.5 KiB`). */
export function formatBytes(value: number): string {
  if (!Number.isFinite(value) || value < 0) {
    return "—";
  }
  if (value < 1024) {
    return `${formatInteger(value)} B`;
  }
  const units = ["KiB", "MiB", "GiB", "TiB"];
  let scaled = value;
  let unit = "B";
  for (const candidate of units) {
    if (scaled < 1024) {
      break;
    }
    scaled /= 1024;
    unit = candidate;
  }
  return `${scaled.toFixed(1)} ${unit}`;
}

/** Formats a duration in milliseconds (`125_000` -> `2m 5s`). */
export function formatDurationMs(value: number): string {
  if (!Number.isFinite(value) || value < 0) {
    return "—";
  }
  const totalSeconds = Math.floor(value / 1000);
  const days = Math.floor(totalSeconds / 86_400);
  const hours = Math.floor((totalSeconds % 86_400) / 3_600);
  const minutes = Math.floor((totalSeconds % 3_600) / 60);
  const seconds = totalSeconds % 60;
  if (days > 0) {
    return `${days}d ${hours}h`;
  }
  if (hours > 0) {
    return `${hours}h ${minutes}m`;
  }
  if (minutes > 0) {
    return `${minutes}m ${seconds}s`;
  }
  return `${seconds}s`;
}

/** Formats an ISO-8601 timestamp as fixed UTC (`2026-08-26 17:40:12Z`). */
export function formatUtcTimestamp(iso: string): string {
  const parsed = Date.parse(iso);
  if (Number.isNaN(parsed)) {
    return iso;
  }
  const date = new Date(parsed);
  const pad = (digits: number, width = 2) => digits.toString(10).padStart(width, "0");
  return (
    `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())} ` +
    `${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())}:${pad(date.getUTCSeconds())}Z`
  );
}
