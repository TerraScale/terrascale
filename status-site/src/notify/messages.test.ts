import { describe, expect, it } from "vitest";
import { email, webhook, type Notice } from "./messages";
import { hmacHex, isEmail, webhookFlavor } from "./webhooks";

const update: Notice = {
  kind: "update",
  channel: "email",
  flavor: "email",
  links: {
    site: "https://s.test",
    manage: "https://s.test/subscribe/manage?id=s&token=t",
    incident: "https://s.test/incidents/00000000000000000000000001",
  },
  incident: {
    id: "00000000000000000000000001",
    kind: "incident",
    title: "Dashboard é down",
    impact: "critical",
    source: "manual",
  },
  update: { status: "investigating", body: "Looking <now>.", createdAt: "2026-09-26T10:00:00Z" },
};

describe("email", () => {
  it("carries subject, text, HTML and one-click unsubscribe headers", () => {
    const message = email(update);
    expect(message.subject).toBe("[TerraScale] Investigating: Dashboard é down");
    expect(message.text).toContain("Investigating: Looking <now>.");
    expect(message.html).toContain("Looking &lt;now&gt;.");
    expect(message.headers).toEqual({
      "List-Unsubscribe": "<https://s.test/subscribe/manage?id=s&token=t>",
      "List-Unsubscribe-Post": "List-Unsubscribe=One-Click",
    });
  });
});

describe("webhooks", () => {
  it("shapes payloads per flavor and signs generic ones", async () => {
    const discord = JSON.parse((await webhook({ ...update, channel: "webhook", flavor: "discord" }, "k")).body);
    expect(discord.embeds[0]).toMatchObject({ title: "Investigating: Dashboard é down", color: 0xf87171 });
    const slack = JSON.parse((await webhook({ ...update, channel: "webhook", flavor: "slack" }, "k")).body);
    expect(slack.text).toContain("*Dashboard é down*");
    const generic = await webhook({ ...update, channel: "webhook", flavor: "generic" }, "k");
    expect(generic.headers["x-terrascale-signature"]).toBe(`sha256=${await hmacHex("k", generic.body)}`);
    expect(JSON.parse(generic.body).type).toBe("incident.update");
  });

  it("accepts Discord, Slack and public HTTPS targets only", () => {
    expect(webhookFlavor("https://discord.com/api/webhooks/123/abc-DEF_9")).toBe("discord");
    expect(webhookFlavor("https://discord.com/channels/1")).toBeNull();
    expect(webhookFlavor("https://localhost./hook")).toBeNull();
    expect(webhookFlavor("https://status.terrascale.tech./hook")).toBeNull();
    expect(webhookFlavor("https://hooks.slack.com/services/T0/B0/xyz")).toBe("slack");
    expect(webhookFlavor("https://example.com/hook")).toBe("generic");
    for (const bad of [
      "http://example.com/hook",
      "https://127.0.0.1/hook",
      "https://[::1]/hook",
      "https://localhost/hook",
      "https://dash.terrascale.tech/hook",
      "https://user:pass@example.com/hook",
      "https://example.com:8443/hook",
      "not a url",
    ]) {
      expect(webhookFlavor(bad)).toBeNull();
    }
  });

  it("checks email addresses loosely", () => {
    expect(isEmail("ops@terrascale.tech")).toBe(true);
    expect(isEmail("no spaces@x.io")).toBe(false);
    expect(isEmail("<a@b.io>")).toBe(false);
  });
});
