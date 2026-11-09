import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { TsAuthWebhookError, verifyWebhook } from "./ts-auth-webhooks.js";

const secret = "whsec_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
const body = '{"type":"user.signed_up","data":{"user_id":"01JDUSER"}}';
const headers = {
  "webhook-id": "msg_01JDWEBHOOK0001",
  "webhook-timestamp": "1765363200",
  "webhook-signature": "v1,myaKa5rQlFeG2BKid6AzyyYZL5dGtB0ogW7TE+30ARg=",
};
const now = () => 1765363200;

describe("verifyWebhook", () => {
  test("accepts a signed delivery and parses the event", () => {
    const event = verifyWebhook(secret, headers, body, now);
    assert.deepEqual(event, { type: "user.signed_up", data: { user_id: "01JDUSER" } });
  });

  test("rejects a tampered body", () => {
    const tampered = body.replace("01JDUSER", "01JDATTACKER");
    assert.throws(() => verifyWebhook(secret, headers, tampered, now), (error) => {
      assert.ok(error instanceof TsAuthWebhookError);
      assert.equal(error.code, "signature_mismatch");
      return true;
    });
  });
});
