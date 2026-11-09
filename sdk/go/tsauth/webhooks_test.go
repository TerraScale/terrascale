package tsauth

import (
	"errors"
	"net/http"
	"testing"
	"time"
)

const webhookTestSecret = "whsec_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="

const webhookTestBody = `{"type":"user.signed_up","data":{"user_id":"01JDUSER"}}`

func webhookTestHeaders(signature string) http.Header {
	headers := http.Header{}
	headers.Set("Webhook-Id", "msg_01JDWEBHOOK0001")
	headers.Set("Webhook-Timestamp", "1765363200")
	headers.Set("Webhook-Signature", signature)
	return headers
}

func webhookTestClock() time.Time {
	return time.Unix(1765363200, 0).UTC()
}

func TestVerifyWebhookAcceptsSignedDelivery(t *testing.T) {
	headers := webhookTestHeaders("v1,myaKa5rQlFeG2BKid6AzyyYZL5dGtB0ogW7TE+30ARg=")
	event, err := VerifyWebhook(webhookTestSecret, headers, []byte(webhookTestBody), webhookTestClock)
	if err != nil {
		t.Fatalf("VerifyWebhook rejected a signed delivery: %v", err)
	}
	if event["type"] != "user.signed_up" {
		t.Fatalf("VerifyWebhook parsed event type %v, want user.signed_up", event["type"])
	}
}

func TestVerifyWebhookRejectsTamperedBody(t *testing.T) {
	headers := webhookTestHeaders("v1,myaKa5rQlFeG2BKid6AzyyYZL5dGtB0ogW7TE+30ARg=")
	tampered := `{"type":"user.signed_up","data":{"user_id":"01JDATTACKER"}}`
	_, err := VerifyWebhook(webhookTestSecret, headers, []byte(tampered), webhookTestClock)
	var webhookErr *WebhookError
	if err == nil {
		t.Fatal("VerifyWebhook accepted a tampered body")
	}
	if !errors.As(err, &webhookErr) || webhookErr.Code != WebhookSignatureMismatch {
		t.Fatalf("VerifyWebhook returned %v, want a WebhookError with code signature_mismatch", err)
	}
}
