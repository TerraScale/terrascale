/* Subscribable feeds: Atom and RSS of incidents, warnings and maintenance
   (one entry per record, its updates as the content), and an iCalendar of
   maintenance windows. */

import type { Recent } from "../db/reads";
import { updatesByIncident, type Incident, type IncidentUpdate } from "../domain/status";
import { escape } from "../html";

const TITLE = "TerraScale Status";

const KINDS = { incident: "Incident", warning: "Warning", maintenance: "Maintenance" } as const;

/** An entry's content: its updates, newest first, as escaped HTML. */
function content(updates: readonly IncidentUpdate[]): string {
  return updates
    .map(
      (update) =>
        `<p><strong>${escape(update.status)}</strong> (${escape(update.createdAt)}): ${escape(update.body)}</p>`,
    )
    .join("");
}

const updated = (recent: Recent, now: number): string => recent.incidents[0]?.updatedAt ?? new Date(now).toISOString();

/** An RFC 822 date; a malformed timestamp falls back to now rather than
    making the whole feed invalid. */
function rfc822(at: string, now: number): string {
  const parsed = Date.parse(at);
  return new Date(Number.isNaN(parsed) ? now : parsed).toUTCString();
}

export function atom(recent: Recent, origin: string, now: number): string {
  const updates = updatesByIncident(recent.updates);
  const entries = recent.incidents.map(
    (incident) => `<entry>
<id>${origin}/incidents/${escape(incident.id)}</id>
<title>${escape(`${KINDS[incident.kind]}: ${incident.title}`)}</title>
<link rel="alternate" href="${origin}/incidents/${escape(incident.id)}"/>
<published>${incident.createdAt}</published>
<updated>${incident.updatedAt}</updated>
<summary>${escape(incident.message)}</summary>
<content type="html">${escape(content(updates.get(incident.id) ?? []))}</content>
</entry>`,
  );
  return `<?xml version="1.0" encoding="utf-8"?>
<feed xmlns="http://www.w3.org/2005/Atom">
<id>${origin}/</id>
<title>${TITLE}</title>
<subtitle>Incidents, warnings and maintenance for TerraScale services.</subtitle>
<link rel="self" href="${origin}/feed.atom"/>
<link rel="alternate" href="${origin}/"/>
<updated>${updated(recent, now)}</updated>
<author><name>TerraScale</name></author>
${entries.join("\n")}
</feed>
`;
}

export function rss(recent: Recent, origin: string, now: number): string {
  const updates = updatesByIncident(recent.updates);
  const date = (at: string) => rfc822(at, now);
  const items = recent.incidents.map(
    (incident) => `<item>
<title>${escape(`${KINDS[incident.kind]}: ${incident.title}`)}</title>
<link>${origin}/incidents/${escape(incident.id)}</link>
<guid isPermaLink="true">${origin}/incidents/${escape(incident.id)}</guid>
<pubDate>${date(incident.updatedAt)}</pubDate>
<description>${escape(content(updates.get(incident.id) ?? []))}</description>
</item>`,
  );
  return `<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom">
<channel>
<title>${TITLE}</title>
<link>${origin}/</link>
<description>Incidents, warnings and maintenance for TerraScale services.</description>
<atom:link href="${origin}/feed.rss" rel="self" type="application/rss+xml"/>
<lastBuildDate>${date(updated(recent, now))}</lastBuildDate>
${items.join("\n")}
</channel>
</rss>
`;
}

/** iCalendar text escaping (RFC 5545 §3.3.11): backslashes first, so the
    escapes added after them stay intact. */
const text = (value: string): string =>
  value
    .replace(/\\/g, "\\\\")
    .replace(/[;,]/g, (char) => `\\${char}`)
    .replace(/\r?\n/g, "\\n");

/** "20261001T020000Z" */
const stamp = (at: string | number): string =>
  new Date(at)
    .toISOString()
    .replace(/[-:]/g, "")
    .replace(/\.\d{3}/, "");

const encoder = new TextEncoder();

/** Folds a content line at 75 octets, continuing with a space. */
function fold(line: string): string {
  if (encoder.encode(line).length <= 75) return line;
  const parts: string[] = [];
  let current = "";
  let size = 0;
  for (const char of line) {
    const length = char.length === 1 && char.charCodeAt(0) < 0x80 ? 1 : encoder.encode(char).length;
    if (size + length > (parts.length === 0 ? 75 : 74)) {
      parts.push(current);
      current = "";
      size = 0;
    }
    current += char;
    size += length;
  }
  parts.push(current);
  return parts.join("\r\n ");
}

export function calendar(recent: Recent, origin: string): string {
  const host = new URL(origin).host;
  const events = recent.incidents
    .filter(
      (incident): incident is Incident & { scheduledStart: string; scheduledEnd: string } =>
        incident.kind === "maintenance" && Boolean(incident.scheduledStart && incident.scheduledEnd),
    )
    .map((incident) =>
      [
        "BEGIN:VEVENT",
        `UID:${incident.id}@${host}`,
        `DTSTAMP:${stamp(incident.updatedAt)}`,
        `DTSTART:${stamp(incident.scheduledStart)}`,
        `DTEND:${stamp(incident.resolvedAt ?? incident.scheduledEnd)}`,
        `SUMMARY:${text(`TerraScale maintenance: ${incident.title}`)}`,
        `DESCRIPTION:${text(incident.message)}`,
        `URL:${origin}/incidents/${incident.id}`,
        "STATUS:CONFIRMED",
        "TRANSP:TRANSPARENT",
        "END:VEVENT",
      ]
        .map(fold)
        .join("\r\n"),
    );
  return [
    "BEGIN:VCALENDAR",
    "VERSION:2.0",
    "PRODID:-//TerraScale//Status//EN",
    "CALSCALE:GREGORIAN",
    "METHOD:PUBLISH",
    fold(`X-WR-CALNAME:${TITLE} maintenance`),
    "X-PUBLISHED-TTL:PT1H",
    ...events,
    "END:VCALENDAR",
    "",
  ].join("\r\n");
}
