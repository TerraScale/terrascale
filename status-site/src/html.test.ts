import { describe, expect, it } from "vitest";
import { html, raw } from "./html";

describe("html", () => {
  it("escapes interpolated text", () => {
    expect(html`<p title="${`"x"`}">${"<script>&"}</p>`.value).toBe('<p title="&quot;x&quot;">&lt;script&gt;&amp;</p>');
  });

  it("keeps nested and trusted markup", () => {
    expect(html`<ul>${["a", "b"].map((item) => html`<li>${item}</li>`)}</ul>${raw("<hr>")}`.value).toBe(
      "<ul><li>a</li><li>b</li></ul><hr>",
    );
  });

  it("drops empty values", () => {
    expect(html`${null}${undefined}${false}${0}`.value).toBe("0");
  });
});
