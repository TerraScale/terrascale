/* Date and number formatting. Pages render at the edge, so every time is
   shown in UTC and says so. */

const dateTime = new Intl.DateTimeFormat("en", {
  dateStyle: "medium",
  timeStyle: "short",
  hourCycle: "h23",
  timeZone: "UTC",
});
const clock = new Intl.DateTimeFormat("en", { hour: "2-digit", minute: "2-digit", hourCycle: "h23", timeZone: "UTC" });
const clockSeconds = new Intl.DateTimeFormat("en", { timeStyle: "medium", hourCycle: "h23", timeZone: "UTC" });
const day = new Intl.DateTimeFormat("en", { month: "short", day: "numeric", timeZone: "UTC" });
const month = new Intl.DateTimeFormat("en", { month: "long", year: "numeric", timeZone: "UTC" });
const monthName = new Intl.DateTimeFormat("en", { month: "short", timeZone: "UTC" });

export const HOUR = 3_600_000;
export const DAY = 24 * HOUR;

/** "Sep 20, 2026, 08:45 UTC" */
export const formatDateTime = (at: number | string): string => `${dateTime.format(new Date(at))} UTC`;

/** "08:45" */
export const formatClock = (at: number): string => clock.format(at);

/** "08:45:12 UTC" */
export const formatClockSeconds = (at: number): string => `${clockSeconds.format(at)} UTC`;

/** "Sep 20" */
export const formatDay = (at: number): string => day.format(at);

/** "September 2026" */
export const formatMonth = (at: number | string): string => month.format(new Date(at));

/** "September ’26" */
export const formatMonthShort = (at: number | string): string => {
  const date = new Date(at);
  return `${monthName.format(date)} ’${String(date.getUTCFullYear()).slice(-2)}`;
};

/** "99.98%", keeping precision where it matters and never rounding to 100. */
export function formatUptime(value: number | null): string {
  if (value === null) return "No data";
  if (value >= 1) return "100%";
  return `${(Math.floor(value * 10_000) / 100).toFixed(2)}%`;
}

/** "2d 4h", "1h 12m", "8m", "under a minute" */
export function formatDuration(ms: number): string {
  const minutes = Math.floor(ms / 60_000);
  if (minutes < 1) return "under a minute";
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  const rest = minutes % 60;
  if (days > 0) return hours > 0 ? `${days}d ${hours}h` : `${days}d`;
  if (hours > 0) return rest > 0 ? `${hours}h ${rest}m` : `${hours}h`;
  return `${rest}m`;
}

/** The start of the UTC hour containing `at`. */
export const startOfHour = (at: number): number => at - (at % HOUR);

/** The start of the UTC day containing `at`. */
export const startOfDay = (at: number): number => at - (at % DAY);
