/* Subscription pages: the form, and every step of double opt-in and
   unsubscribing. Every action is a POST form, so mail scanners that follow
   links cannot confirm or unsubscribe anyone. */

import type { Component } from "../domain/status";
import { html, raw, type Html } from "../html";
import { card, empty } from "./components";
import type { Page } from "./layout";

export interface SubscribeForm {
  components: readonly Component[];
  error?: string;
  values?: { channel?: string; target?: string; components?: readonly string[] };
}

function shell(title: string, body: Html): Page {
  return {
    title: `${title} · TerraScale Status`,
    description: "Get TerraScale incident and maintenance updates by email or webhook.",
    path: null,
    body: html`<main id="main" class="ts-page st-narrow">${body}</main>`,
  };
}

const head = (eyebrow: string, title: string, detail: string | Html) => html`<header class="st-page-head">
  <p class="ts-eyebrow">${eyebrow}</p>
  <h1 class="st-title">${title}</h1>
  <p class="ts-muted">${detail}</p>
</header>`;

export function subscribeForm(form: SubscribeForm): Page {
  const chosen = new Set(form.values?.components ?? []);
  const channel = form.values?.channel ?? "email";
  const option = (value: string, label: string) =>
    html`<label class="st-choice"><input type="radio" name="channel" value="${value}"${channel === value ? raw(" checked") : ""}> ${label}</label>`;
  return shell(
    "Subscribe",
    html`${head("Stay informed", "Subscribe to updates", "Every incident update and maintenance window, as it is posted. Automatic incidents are only sent once they have lasted five minutes.")}
  ${form.error ? html`<article class="ts-alert" data-state="outage" role="alert"><p class="ts-alert-body">${form.error}</p></article>` : ""}
  ${card(
    { id: "subscribe", title: "Where to send them" },
    html`<form class="st-form" method="post" action="/subscribe">
    <fieldset class="st-choices"><legend class="ts-sr-only">Channel</legend>
      ${option("email", "Email")}${option("webhook", "Webhook (Discord, Slack or any HTTPS URL)")}
    </fieldset>
    <label class="st-field"><span>Email address or webhook URL</span>
      <input class="ts-input" name="target" required maxlength="500" autocomplete="email" value="${form.values?.target ?? ""}" placeholder="you@example.com or https://discord.com/api/webhooks/…">
    </label>
    <fieldset class="st-choices st-components"><legend>Components <span class="ts-meta">(none selected: all of them)</span></legend>
      ${form.components.map(
        (component) =>
          html`<label class="st-choice"><input type="checkbox" name="components" value="${component.id}"${chosen.has(component.id) ? raw(" checked") : ""}> <span>${component.name}${component.lifecycle === "planned" ? html` <span class="ts-meta">(coming soon)</span>` : ""}</span></label>`,
      )}
    </fieldset>
    <label class="st-honeypot" aria-hidden="true">Leave this empty <input name="website" tabindex="-1" autocomplete="off"></label>
    <button class="ts-button" type="submit">Subscribe</button>
    <p class="ts-meta">You will get one message asking you to confirm. Every message has an unsubscribe link. Generic webhooks are signed with an <code>x-terrascale-signature</code> HMAC-SHA256 header, keyed by the <code>signingKey</code> in their confirmation request.</p>
  </form>`,
  )}`,
  );
}

export function sentPage(channel: "email" | "webhook"): Page {
  return shell(
    "Check your inbox",
    html`${head("Almost there", channel === "email" ? "Check your inbox" : "Check your channel", "We sent a confirmation link. Updates start once you confirm; unconfirmed subscriptions are dropped after a week.")}`,
  );
}

function action(path: string, id: string, token: string, label: string, detail: string): Html {
  return html`<form class="st-form" method="post" action="${path}">
  <input type="hidden" name="id" value="${id}"><input type="hidden" name="token" value="${token}">
  <p class="ts-muted">${detail}</p>
  <button class="ts-button" type="submit">${label}</button>
</form>`;
}

/** How a target is shown. A webhook URL is itself a credential that lets
    anyone post to the channel, and these pages are reachable by whoever
    sees the channel's messages, so only its host and last characters show. */
export function shownTarget(target: string): string {
  if (!target.startsWith("https://")) return target;
  const url = new URL(target);
  return `${url.host}/…${target.slice(-4)}`;
}

export function confirmPage(id: string, token: string, target: string): Page {
  return shell(
    "Confirm subscription",
    html`${head("Subscriptions", "Confirm your subscription", html`Updates will go to <strong>${shownTarget(target)}</strong>.`)}
  ${action("/subscribe/confirm", id, token, "Confirm subscription", "One click and you are subscribed.")}`,
  );
}

export function confirmedPage(manage: string): Page {
  return shell(
    "Subscribed",
    html`${head("Subscriptions", "You are subscribed", "Updates will arrive as they are posted.")}
  <p><a class="ts-link" href="${manage}">Manage or unsubscribe</a></p>`,
  );
}

export function managePage(
  id: string,
  token: string,
  target: string,
  components: readonly string[],
  names: ReadonlyMap<string, string>,
): Page {
  const following =
    components.length === 0 ? "every component" : components.map((entry) => names.get(entry) ?? entry).join(", ");
  return shell(
    "Manage subscription",
    html`${head("Subscriptions", "Your subscription", html`Updates about ${following} go to <strong>${shownTarget(target)}</strong>.`)}
  ${action("/subscribe/unsubscribe", id, token, "Unsubscribe", "Stop all updates. You can subscribe again at any time.")}`,
  );
}

export function unsubscribedPage(): Page {
  return shell(
    "Unsubscribed",
    html`${head("Subscriptions", "You are unsubscribed", "No more updates will be sent.")}<p><a class="ts-link" href="/subscribe">Subscribe again</a></p>`,
  );
}

export function invalidLinkPage(): Page {
  return shell(
    "Link not valid",
    empty(
      "This link is not valid",
      "It may have expired, or the subscription no longer exists. You can subscribe again.",
    ),
  );
}

export function unavailablePage(): Page {
  return shell(
    "Subscriptions unavailable",
    empty(
      "Subscriptions are not available yet",
      "Follow the Atom feed or the maintenance calendar on the integrations page meanwhile.",
    ),
  );
}
