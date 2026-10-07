package core_test

import (
	stdjson "encoding/json"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

// TestMaskSecretsHidesCredentialsOfBothSystemDatabases covers the members
// ts-auth (camelCase) and ts-dashboard (snake_case and Go field names) keep
// credentials in, at any depth, and names another service may use.
func TestMaskSecretsHidesCredentialsOfBothSystemDatabases(t *testing.T) {
	documents := []string{
		`{"id":"u1","email":"ana@example.com","passwordHash":"$argon2id$v=19$a","authEpoch":2}`,
		`{"instanceId":"terrascale","tokenHash":"aa11","expiresAt":"2026-10-06"}`,
		`{"keys":[{"id":"k1","algorithm":"ES256","secretBox":"sealed"}]}`,
		`{"clientId":"ts-dashboard","clientSecret":"cs","properties":{}}`,
		`{"type":"refresh_token","payload":"eyJ","referenceId":"r1"}`,
		`{"kind":"record","token_hash":"bb22","data":{"User":{"PasswordHash":"h","TOTPSeed":"s"},"Refresh":"{\"token\":\"rt\"}"}}`,
		`{"route":"api","upstreamCredentials":{"user":"u"},"apiKey":"ak","session_cookie":"ck","nonce":"nn","signingKey":"sk"}`,
	}
	for _, doc := range documents {
		var value any
		if err := stdjson.Unmarshal([]byte(doc), &value); err != nil {
			t.Fatal(err)
		}
		masked, err := stdjson.Marshal(core.MaskSecrets(value))
		if err != nil {
			t.Fatal(err)
		}
		for _, secret := range []string{"argon2id", "aa11", "sealed", `"cs"`, "eyJ", `"r1"`, "bb22", `"h"`, `"s"`, `\"rt\"`, `"u"`, `"ak"`, `"ck"`, `"nn"`, `"sk"`} {
			if strings.Contains(string(masked), secret) {
				t.Errorf("%s still shows %s", masked, secret)
			}
		}
		for _, kept := range []string{"ana@example.com", "terrascale", "ts-dashboard", "record", "api"} {
			if strings.Contains(doc, kept) && !strings.Contains(string(masked), kept) {
				t.Errorf("%s lost %s", masked, kept)
			}
		}
	}
}
