import { createTsAuthClient } from "@terrascale/sdk/ts-auth";
import { describe, expect, it, vi } from "vitest";
import { isSdkProbeConfig, probeCollection, probeCredentials, type SdkProbeConfig } from "./sdk";

const key = `tsk_${"a".repeat(20)}_${"b".repeat(52)}`;
const config = (): SdkProbeConfig => ({
  origin: "https://api.terrascale.tech",
  issuer: "https://gru.auth.terrascale.tech/terrascale",
  project: "01M4E7G1W82VKFPF5FJ9AVK1CQ",
  key,
  database: "canary",
  collection: "canary",
  document: "01M4E7G1W82VKFPF5FJ9AVK1D9",
});

describe("TS-603 SDK draft", () => {
  it("accepts platform credentials and rejects legacy identity and unsafe targets", () => {
    expect(isSdkProbeConfig("documents", config())).toBe(true);
    expect(
      isSdkProbeConfig("management", {
        origin: config().origin,
        issuer: config().issuer,
        project: config().project,
        key,
      }),
    ).toBe(true);
    for (const patch of [
      { key: "tb_fake_key" },
      { key: "tsk_invalid" },
      { key: key.replace("a", "i") },
      { issuer: "http://auth.example/terrascale" },
      { issuer: `${config().issuer}/` },
      { issuer: `${config().issuer}?token=secret` },
      { issuer: "https://user:secret@auth.example/terrascale" },
      { issuer: "https://auth.example/customer" },
      { origin: `${config().origin}/` },
      { project: "not-a-project" },
      { database: "Customer Data" },
      { collection: "../customer" },
      { document: "not-an-id" },
      { userId: "acting-user" },
      { organization: "old-org" },
    ])
      expect(isSdkProbeConfig("documents", { ...config(), ...patch })).toBe(false);
  });

  it("prepares the published API-key grant and least scopes without impersonation", () => {
    expect(probeCredentials("documents", config())).toEqual({
      issuer: config().issuer,
      clientId: key.slice(0, 24),
      clientSecret: key,
      request: {
        grant_type: "client_credentials",
        project: config().project,
        scope: "sql:read sql:write",
      },
    });
    expect(probeCredentials("management", config()).request.scope).toBe("databases:read");
    expect(() => probeCredentials("documents", { ...config(), key: "tb_fake_key" })).toThrow("Invalid probe config");
  });

  it("records the JWT SDK blocker without dispatching or substituting a data key", () => {
    const fetcher = vi.fn();
    expect(() => probeCollection(config(), "header.payload.signature", fetcher)).toThrow("token must be a tb_ API key");
    expect(fetcher).not.toHaveBeenCalled();
  });

  it("records the platform-grant SDK blocker without using raw HTTP", () => {
    const fetcher = vi.fn();
    const client = createTsAuthClient({
      apiBaseUrl: "https://gru.auth.terrascale.tech",
      instanceSlug: "terrascale",
      fetch: fetcher,
    });
    const credentials = probeCredentials("documents", config());
    // Deliberate compile-time check: upgrade this gate when TS-592 admits
    // the published platform request (project, rather than OAuth resource).
    // @ts-expect-error SDK 0.0.2 does not implement the TS-598 platform grant.
    expect(() => client.oidc.token(credentials.clientId, credentials.clientSecret, credentials.request)).toThrow(
      TypeError,
    );
    expect(fetcher).not.toHaveBeenCalled();
  });
});
