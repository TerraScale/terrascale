import { dashboardStates, type PagePattern } from '../pages/inventory';
import { auth, dash } from './stack';

export type Link = { kind: string; url: string };

const contextParameters = new Set(['organizationId', 'projectId']);
const chartWindows = new Set(['range', 'reads', 'writes', 'storage', 'latency']);

const journeyPages: readonly PagePattern[] = [
  { path: /^\/auth\/mfa-sessions$/, query: { saved: /^1$/ } },
  { path: /^\/databases\/[0-9A-HJKMNP-TV-Z]{26}\/settings$/, query: { renamed: /^1$/ } },
  { path: /^\/organization$/, query: { newOrganization: /^(payment|project)$/, payment: /^(success|canceled)$/, draft_id: /^([0-9A-HJKMNP-TV-Z]{26})?$/ } },
  { path: /^\/onboarding$/ },
  { path: /^\/storage$/, query: { bucket: /^[0-9A-HJKMNP-TV-Z]{26}$/, tab: /^(settings)?$/ } },
  { path: /^\/account-deleted$/ },
  { path: /^\/profile$/, query: { tab: /^access-tokens$/, status: /^token-revoked$/ } },
  { path: /^\/profile$/, query: { tab: /^profile$/, status: /^profile-saved$/ } },
  { path: /^\/organization$/, query: { tab: /^billing$/, payment: /^success$/ } },
  { path: /^\/organization$/, query: { tab: /^members$/, invitation: /^accepted$/ } },
  { path: /^\/organizations\/choose$/, query: { returnTo: /^\/$/ } },
];

const hostedPages: readonly RegExp[] = [
  /^\/ui\/[0-9A-HJKMNP-TV-Z]{26}\/(sign-in|sign-up|security)$/,
  /^\/ui\/terrascale\/device$/,
  /^\/ui\/terrascale\/sign-up$/,
  /^\/ui\/[0-9A-HJKMNP-TV-Z]{26}\/(sign-up|sign-in|reset)$/,
  /^\/ui\/terrascale\/sign-in$/,
  /^\/ui\/terrascale\/reset$/,
  /^\/ui\/terrascale\/security$/,
  /^\/ui\/terrascale\/step-up\/[^/]+$/,
];

const excludedPages: readonly { path: RegExp; reason: string }[] = [
  { path: /^\/(login|register)$/, reason: 'redirects to the hosted sign-in or sign-up page' },
  { path: /^\/logout$/, reason: 'sign-out: ends the session' },
  { path: /^\/login\/callback$/, reason: 'OIDC callback: redirect only' },
  { path: /^\/auth\/step-up\/callback$/, reason: 'confirmation callback: redirect only' },
  { path: /^\/billing(\/portal|\/checkout\/return)?$/, reason: 'redirects to Stripe or back to the billing tab' },
  { path: /^\/audit$/, reason: 'redirects to the organization audit tab' },
  { path: /^\/audit\/export$/, reason: 'download' },
];

const dashboardPages: readonly PagePattern[] = [...dashboardStates.map((state) => state.covers), ...journeyPages];

function covered(pattern: PagePattern, url: URL, query: [string, string][]): boolean {
  if (!pattern.path.test(url.pathname)) return false;
  const allowed = pattern.query ?? {};
  if (Object.keys(allowed).some((key) => !url.searchParams.has(key) && !allowed[key].test(''))) return false;
  return query.every(([key, value]) => allowed[key]?.test(value) ?? false);
}

export function checkLinks(links: Link[], page: string): string[] {
  const origin = new URL(page).origin;
  const problems = new Set<string>();
  for (const link of links) {
    const url = new URL(link.url);
    if (url.origin !== origin) continue;
    if (origin === auth) {
      if (!hostedPages.some((pattern) => pattern.test(url.pathname))) problems.add(`${link.kind} to ${origin}${url.pathname} matches no audited hosted page`);
      continue;
    }
    if (origin !== dash) continue;
    if (excludedPages.some((excluded) => excluded.path.test(url.pathname))) continue;
    const query = [...url.searchParams].filter(([key]) => !contextParameters.has(key) && !chartWindows.has(key));
    if (dashboardPages.some((pattern) => covered(pattern, url, query))) continue;
    const search = new URLSearchParams(query).toString();
    problems.add(`${link.kind} to ${url.pathname}${search ? `?${search}` : ''} matches no audited page and no exclusion`);
  }
  return [...problems];
}
