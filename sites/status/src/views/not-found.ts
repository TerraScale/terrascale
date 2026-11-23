import { html } from "../html";
import { empty } from "./components";
import type { Page } from "./layout";

export function notFoundPage(): Page {
  return {
    title: "Page not found · TerraScale Status",
    description: "This page does not exist.",
    path: null,
    body: html`<main id="main" class="ts-page">
  <header class="st-page-head">
    <p class="ts-eyebrow">Error 404</p>
    <h1 class="st-title">Page not found</h1>
  </header>
  ${empty(
    "Nothing lives at this address",
    "The page may have moved. The current platform status is on the home page.",
    "search-x",
    html`<a class="ts-button" href="/">Back to current status</a>`,
  )}
</main>`,
  };
}
