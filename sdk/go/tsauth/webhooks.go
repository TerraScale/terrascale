package tsauth

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"
)

type WebhookErrorCode string

const (
	WebhookMissingHeaders          WebhookErrorCode = "missing_headers"
	WebhookInvalidSecret           WebhookErrorCode = "invalid_secret"
	WebhookInvalidTimestamp        WebhookErrorCode = "invalid_timestamp"
	WebhookTimestampOutOfTolerance WebhookErrorCode = "timestamp_out_of_tolerance"
	WebhookSignatureMismatch       WebhookErrorCode = "signature_mismatch"
	WebhookInvalidBody             WebhookErrorCode = "invalid_body"
)

type WebhookError struct {
	Code    WebhookErrorCode
	Message string
}

func (e *WebhookError) Error() string {
	return fmt.Sprintf("tsauth: webhook %s: %s", e.Code, e.Message)
}

const webhookToleranceSeconds = 300

func VerifyWebhook(secret string, headers http.Header, rawBody []byte, now ...func() time.Time) (map[string]any, error) {
	clock := time.Now
	if len(now) > 0 {
		clock = now[0]
	}
	id := headers.Get("Webhook-Id")
	timestamp := headers.Get("Webhook-Timestamp")
	signature := headers.Get("Webhook-Signature")
	if id == "" || timestamp == "" || signature == "" {
		return nil, &WebhookError{Code: WebhookMissingHeaders, Message: "The delivery must carry webhook-id, webhook-timestamp and webhook-signature."}
	}
	unixSeconds, err := strconv.ParseUint(timestamp, 10, 64)
	if err != nil {
		return nil, &WebhookError{Code: WebhookInvalidTimestamp, Message: "webhook-timestamp must be unix seconds."}
	}
	drift := clock().Unix() - int64(unixSeconds)
	if drift < 0 {
		drift = -drift
	}
	if drift > webhookToleranceSeconds {
		return nil, &WebhookError{Code: WebhookTimestampOutOfTolerance, Message: "webhook-timestamp is more than 300 seconds from now."}
	}
	payload, ok := strings.CutPrefix(secret, "whsec_")
	if !ok || payload == "" {
		return nil, &WebhookError{Code: WebhookInvalidSecret, Message: "The secret must be the whsec_ value answered once at registration or rotation."}
	}
	key, err := base64.StdEncoding.DecodeString(payload)
	if err != nil || len(key) == 0 {
		return nil, &WebhookError{Code: WebhookInvalidSecret, Message: "The secret's whsec_ payload must be base64."}
	}
	mac := hmac.New(sha256.New, key)
	mac.Write([]byte(id))
	mac.Write([]byte("."))
	mac.Write([]byte(timestamp))
	mac.Write([]byte("."))
	mac.Write(rawBody)
	digest := mac.Sum(nil)
	for _, entry := range strings.Split(signature, " ") {
		value, ok := strings.CutPrefix(entry, "v1,")
		if !ok {
			continue
		}
		candidate, err := base64.StdEncoding.DecodeString(value)
		if err != nil {
			continue
		}
		if hmac.Equal(digest, candidate) {
			var event map[string]any
			if err := json.Unmarshal(rawBody, &event); err != nil {
				return nil, &WebhookError{Code: WebhookInvalidBody, Message: "The delivery body is not valid JSON."}
			}
			return event, nil
		}
	}
	return nil, &WebhookError{Code: WebhookSignatureMismatch, Message: "No v1 signature entry matched the delivery."}
}
