/* The page shell: head, top bar, and footer. Vite emits the shared
   stylesheet as a cacheable asset. */

import mark from "../assets/mark.svg?raw";
import type { Status } from "../domain/status";
import { html, raw, type Html } from "../html";
import stylesheet from "../styles/index.css?url";
import chartScript from "../assets/chart-client.js?url";
import clientScript from "../assets/client.js?url";
import { REFRESH_SECONDS } from "./client";

const NAV = [
  { href: "/", label: "Overview", icon: "M3 12h4l3-8 4 16 3-8h4" },
  { href: "/history", label: "History", icon: "M3 11a9 9 0 1 1 2 7 M3 4v7h7 M12 7v5l3 2" },
  { href: "/uptime", label: "Uptime", icon: "M4 19V9 M12 19V4 M20 19v-7" },
  { href: "/ping", label: "Ping", icon: "M3 8a15 15 0 0 1 18 0 M6 12a10 10 0 0 1 12 0 M9 16a5 5 0 0 1 6 0 M12 20h.01" },
  {
    href: "/maintenance",
    label: "Maintenance",
    icon: "M14 6a5 5 0 0 0-6 6L3 17a3 3 0 0 0 4 4l5-5a5 5 0 0 0 6-6l-4 4-4-4 4-4Z",
  },
  { href: "/subscribe", label: "Subscribe", icon: "M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9 M10 21h4" },
] as const;

function icon(path: string): Html {
  return html`<svg class="st-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${path}"></path></svg>`;
}

function navigation(page: Page): Html {
  return html`${NAV.filter((item) => item.href !== "/subscribe").map((item) => html`<a href="${item.href}"${page.path === item.href ? raw(' aria-current="page"') : ""}>${icon(item.icon)}${item.label}</a>`)}
    <a href="https://docs.terrascale.tech">${icon("M12 5v16 M3 3h5l4 2 4-2h5v16h-5l-4 2-4-2H3Z")}Docs</a>
    <a href="https://dash.terrascale.tech">${icon("M3 3h7v7H3Z M14 3h7v7h-7Z M3 14h7v7H3Z M14 14h7v7h-7Z")}Dashboard</a>`;
}

export interface Page {
  title: string;
  description: string;
  /** The nav entry the page belongs to. */
  path: (typeof NAV)[number]["href"] | null;
  /** The page's <main>; live pages mark it data-live to refresh in place. */
  body: Html;
  live?: boolean;
  /** Only the page itself, without the top bar and footer (the widget). */
  bare?: boolean;
  /** The overall status, for the footer's live status link. */
  status?: Status;
}

/* The sticky, frosted top bar. Wide screens show the page links inline;
   phones fold them into a menu that works without scripts. The time zone
   control needs the client script, so it starts hidden. */
function top(page: Page): Html {
  return html`<a class="ts-skip-link" href="#main">Skip to main content</a>
<header class="st-top">
  <div class="st-frame">
    <a class="st-brand" href="/" aria-label="TerraScale status home">${raw(mark)}<span class="st-brand-word">TerraScale</span><span class="st-brand-tag">Status</span></a>
    <nav class="st-nav st-desktop-nav" aria-label="Status pages">${navigation(page)}</nav>
    <div class="st-top-end">
      <label class="st-zone" hidden>
        <span class="ts-sr-only">Time zone</span>
        <select id="st-zone" class="ts-select ts-select-sm"></select>
      </label>
      <a class="ts-button st-subscribe" href="/subscribe">${icon(NAV[5].icon)}Subscribe</a>
      <details class="st-mobile-menu">
        <summary aria-label="Navigation">${icon("M3 6h18 M3 12h18 M3 18h18")}</summary>
        <nav class="st-nav" aria-label="Mobile status pages">${navigation(page)}</nav>
      </details>
    </div>
  </div>
</header>`;
}

const SOCIALS = [
  {
    href: "https://github.com/TerraScale",
    label: "GitHub",
    filled: true,
    d: "M12 .297c-6.63 0-12 5.373-12 12c0 5.303 3.438 9.8 8.205 11.385c.6.113.82-.258.82-.577c0-.285-.01-1.04-.015-2.04c-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729c1.205.084 1.838 1.236 1.838 1.236c1.07 1.835 2.809 1.305 3.495.998c.108-.776.417-1.305.76-1.605c-2.665-.3-5.466-1.332-5.466-5.93c0-1.31.465-2.38 1.235-3.22c-.135-.303-.54-1.523.105-3.176c0 0 1.005-.322 3.3 1.23c.96-.267 1.98-.399 3-.405c1.02.006 2.04.138 3 .405c2.28-1.552 3.285-1.23 3.285-1.23c.645 1.653.24 2.873.12 3.176c.765.84 1.23 1.91 1.23 3.22c0 4.61-2.805 5.625-5.475 5.92c.42.36.81 1.096.81 2.22c0 1.606-.015 2.896-.015 3.286c0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12",
  },
  {
    href: "https://linkedin.com/company/terrascale",
    label: "LinkedIn",
    filled: true,
    d: "M20.447 20.452h-3.554v-5.569c0-1.328-.027-3.037-1.852-3.037c-1.853 0-2.136 1.445-2.136 2.939v5.667H9.351V9h3.414v1.561h.046c.477-.9 1.637-1.85 3.37-1.85c3.601 0 4.267 2.37 4.267 5.455v6.286zM5.337 7.433a2.06 2.06 0 0 1-2.063-2.065a2.064 2.064 0 1 1 2.063 2.065m1.782 13.019H3.555V9h3.564zM22.225 0H1.771C.792 0 0 .774 0 1.729v20.542C0 23.227.792 24 1.771 24h20.451C23.2 24 24 23.227 24 22.271V1.729C24 .774 23.2 0 22.222 0z",
  },
  {
    href: "mailto:support@terrascale.tech",
    label: "Email",
    filled: false,
    d: "M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Z M22 7l-8.991 5.727a2 2 0 0 1-2.009 0L2 7",
  },
] as const;

const FOOTER_COLUMNS = [
  {
    key: "Product",
    links: [
      { href: "https://terrascale.tech/#features", label: "Features" },
      { href: "https://terrascale.tech/pricing", label: "Pricing" },
      { href: "https://docs.terrascale.tech/changelog/", label: "Changelog" },
    ],
  },
  {
    key: "Company",
    links: [
      { href: "https://terrascale.tech/about", label: "About" },
      { href: "https://terrascale.tech/contact", label: "Contact" },
      { href: "https://docs.terrascale.tech/legal/terms", label: "Terms" },
      { href: "https://docs.terrascale.tech/legal/privacy", label: "Privacy" },
      { href: "https://docs.terrascale.tech/legal/dpa", label: "DPA" },
    ],
  },
  {
    key: "Resources",
    links: [
      { href: "https://docs.terrascale.tech", label: "Documentation" },
      { href: "https://api.terrascale.tech/scalar/", label: "API Reference" },
    ],
  },
  {
    key: "Status",
    links: [
      { href: "/subscribe", label: "Subscribe" },
      { href: "/integrations", label: "Integrations" },
      { href: "/feed.atom", label: "Feed" },
      { href: "/api/v1/status", label: "JSON API" },
    ],
  },
] as const;

const EXTERNAL = raw(' target="_blank" rel="external noopener noreferrer"');

function socialGlyph(social: (typeof SOCIALS)[number]): Html {
  if (social.filled) {
    return html`<svg class="st-f2-social-icon" viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="${social.d}"></path></svg>`;
  }
  return html`<svg class="st-f2-social-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${social.d}"></path></svg>`;
}

function footerLink(link: { href: string; label: string }): Html {
  return html`<a class="st-f2-link" href="${link.href}"${link.href.startsWith("http") ? EXTERNAL : ""}>${link.label}</a>`;
}

function bottom(page: Page, year: number): Html {
  return html`<div id="st-live" class="ts-sr-only" aria-live="polite"></div>
<footer class="st-foot2">
  <div class="st-frame st-f2-grid">
    <div class="st-f2-brand">
      <a class="st-f2-logo" href="https://terrascale.tech"${EXTERNAL} aria-label="TerraScale home">${raw(mark)}<span class="st-f2-word">TerraScale</span></a>
      <p class="st-f2-tagline">Multi-region database</p>
      <ul class="st-f2-socials">
        ${SOCIALS.map((social) => html`<li><a class="st-f2-social" href="${social.href}"${social.href.startsWith("mailto:") ? raw(' rel="external noopener noreferrer"') : EXTERNAL} aria-label="${social.label}">${socialGlyph(social)}</a></li>`)}
      </ul>
      <a class="ts-button st-f2-cta" href="https://dash.terrascale.tech/register"${EXTERNAL}>Create account</a>
    </div>
    ${FOOTER_COLUMNS.map(
      (column) => html`<nav class="st-f2-col" aria-label="${column.key}">
      <div class="ts-label">${column.key}</div>
      <ul class="st-f2-list">
        ${column.links.map((link) => html`<li>${footerLink(link)}</li>`)}
      </ul>
    </nav>`,
    )}
  </div>
  <div class="st-frame">
    <div class="st-f2-meta"><span>© ${year} TerraScale. All rights reserved.</span></div>
  </div>
</footer>`;
}

/** The page shell. ts-status is dark only: data-theme="dark" picks the
    dark values of whichever design tokens file is in place. */
export function shell(page: Page, year: number): Html {
  return html`<!doctype html>
<html lang="en" data-theme="dark">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="dark">
<meta name="theme-color" content="#151515">
<title>${page.title}</title>
<meta name="description" content="${page.description}">
<link rel="icon" type="image/svg+xml" href="/icon.svg">
<link rel="alternate" type="application/atom+xml" title="TerraScale Status" href="/feed.atom">
<link rel="preload" href="/fonts/Geist-Variable.woff2" as="font" type="font/woff2" crossorigin>
<link rel="stylesheet" href="${stylesheet}">
${page.live ? html`<noscript><meta http-equiv="refresh" content="${REFRESH_SECONDS}"></noscript>` : ""}
</head>
<body>
${page.bare ? "" : top(page)}
${page.body}
${page.bare ? "" : bottom(page, year)}
<script defer src="${clientScript}"></script>
<script defer src="${chartScript}"></script>
</body>
</html>`;
}

/** Keep repeated tooltip records out of the HTML budget. SVG labels and
    native date/state tooltips remain readable without JavaScript. */
export async function compactBarData(markup: string): Promise<string> {
  if (!markup.includes("</main>")) return markup;
  const data: unknown[] = [];
  const body = markup.replace(
    /<script type="application\/json" data-bar-record>([^]*?)<\/script>/g,
    (_, payload: string) => {
      data.push(JSON.parse(payload));
      return "";
    },
  );
  if (data.length === 0) return markup;
  const stream = new Blob([JSON.stringify(data)]).stream().pipeThrough(new CompressionStream("gzip"));
  const bytes = new Uint8Array(await new Response(stream).arrayBuffer());
  const encoded = btoa(Array.from(bytes, (byte) => String.fromCharCode(byte)).join(""));
  return body.replace("</main>", `<script type="application/octet-stream" data-bar-data>${encoded}</script></main>`);
}
