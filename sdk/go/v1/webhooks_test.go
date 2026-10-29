package v1_test

import (
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

const webhookPath = parityDatabase + "/collections/users/webhooks"
const webhookJSON = `{"id":"` + parityID + `","url":"https://receiver.example/events","createdAt":"2026-10-23T12:00:00Z","status":"active"}`

func TestWebhookContracts(t *testing.T) {
	t.Run("create", func(t *testing.T) {
		// This is an intentionally fake signing secret, never a live credential.
		const secret = "fixture-signing-secret"
		reply := webhookJSON[:len(webhookJSON)-1] + `,"secret":"` + secret + `"}`
		col := parityClient(t, http.MethodPost, webhookPath, "", `{"url":"https://receiver.example/events"}`, reply, http.StatusCreated).Database(parityProject, "main").Collection("users")
		out, err := col.CreateWebhook(t.Context(), "https://receiver.example/events")
		if err != nil {
			t.Fatal(err)
		}
		if out.ID != parityID || out.URL != "https://receiver.example/events" || out.CreatedAt.IsZero() || out.Status != "active" || out.Secret != secret {
			t.Fatal("creation response did not preserve the contract")
		}
	})
	for _, paged := range []bool{false, true} {
		t.Run(fmt.Sprintf("list/paged=%t", paged), func(t *testing.T) {
			opts, query := tb.WebhookListOptions{}, ""
			if paged {
				opts, query = tb.WebhookListOptions{After: parityID, Limit: 100}, "after="+parityID+"&limit=100"
			}
			col := parityClient(t, http.MethodGet, webhookPath, query, "", "["+webhookJSON+"]", http.StatusOK).Database(parityProject, "main").Collection("users")
			out, err := col.Webhooks(t.Context(), opts)
			if err != nil || len(out) != 1 || out[0].ID != parityID || out[0].CreatedAt.IsZero() {
				t.Fatalf("list failed: %v", err)
			}
		})
	}
	t.Run("delete", func(t *testing.T) {
		col := parityClient(t, http.MethodDelete, webhookPath+"/"+parityID, "", "", "", http.StatusNoContent).Database(parityProject, "main").Collection("users")
		if err := col.DeleteWebhook(t.Context(), parityID); err != nil {
			t.Fatal(err)
		}
	})
	for _, attempted := range []bool{false, true} {
		t.Run(fmt.Sprintf("deliveries/attempted=%t", attempted), func(t *testing.T) {
			fields := `"lastAttemptAt":null,"nextAttemptAt":null,"httpStatus":null,"errorCode":null`
			if attempted {
				fields = `"lastAttemptAt":"2026-10-23T12:00:00Z","nextAttemptAt":"2026-10-23T12:00:01Z","httpStatus":503,"errorCode":"http_error"`
			}
			reply := `[{"id":"` + parityID + `","createdAt":"2026-10-23T12:00:00Z","status":"pending","attempts":2147483648,` + fields + `}]`
			col := parityClient(t, http.MethodGet, webhookPath+"/"+parityID+"/deliveries", "after="+parityID+"&limit=1", "", reply, http.StatusOK).Database(parityProject, "main").Collection("users")
			out, err := col.WebhookDeliveries(t.Context(), parityID, tb.WebhookListOptions{After: parityID, Limit: 1})
			if err != nil || len(out) != 1 {
				t.Fatalf("deliveries failed: %v", err)
			}
			d := out[0]
			if d.ID != parityID || d.CreatedAt.IsZero() || d.Status != "pending" || d.Attempts != 2147483648 || (d.LastAttemptAt != nil) != attempted || (d.NextAttemptAt != nil) != attempted || (d.HTTPStatus != nil) != attempted || (d.ErrorCode != nil) != attempted {
				t.Fatal("delivery fields did not preserve nullability")
			}
			if attempted && (*d.HTTPStatus != 503 || *d.ErrorCode != "http_error") {
				t.Fatal("delivery failure fields were lost")
			}
		})
	}
}

func TestCreatedWebhookFormattingRedactsSecret(t *testing.T) {
	hook := tb.CreatedWebhook{Secret: "synthetic-secret-never-log"}
	for _, value := range []any{hook, &hook} {
		for _, format := range []string{"%v", "%+v", "%#v", "%s", "%q"} {
			got := fmt.Sprintf(format, value)
			if strings.Contains(got, hook.Secret) || !strings.Contains(got, "secret redacted") {
				t.Fatal("webhook formatting must redact the signing secret")
			}
		}
	}
}

func TestWebhookValidationBeforeHTTP(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { t.Error("unexpected HTTP request") }))
	defer server.Close()
	c, err := tb.New(tb.Config{BaseURL: server.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	col := c.Database(parityProject, "main").Collection("users")
	for _, opts := range []tb.WebhookListOptions{{After: "not-ulid"}, {Limit: -1}, {Limit: 101}} {
		if _, err := col.Webhooks(t.Context(), opts); err == nil {
			t.Fatal("accepted invalid list options")
		}
		if _, err := col.WebhookDeliveries(t.Context(), parityID, opts); err == nil {
			t.Fatal("accepted invalid delivery options")
		}
	}
	for _, id := range []string{"", "not-ulid", "01arz3ndektsv4rrffq69g5faw", "../deliveries"} {
		if err := col.DeleteWebhook(t.Context(), id); err == nil {
			t.Fatal("accepted invalid webhook ID")
		}
		if _, err := col.WebhookDeliveries(t.Context(), id, tb.WebhookListOptions{}); err == nil {
			t.Fatal("accepted invalid delivery webhook ID")
		}
	}
	for _, col := range []*tb.Collection{c.Database("invalid", "main").Collection("users"), c.Database(parityProject, "../main").Collection("users"), c.Database(parityProject, "main").Collection("Users")} {
		if _, err := col.CreateWebhook(t.Context(), "https://receiver.example/events"); err == nil {
			t.Fatal("accepted invalid collection address")
		}
		if _, err := col.Webhooks(t.Context(), tb.WebhookListOptions{}); err == nil {
			t.Fatal("accepted invalid collection address")
		}
	}
}

func TestWebhookErrorsAndResponseValidation(t *testing.T) {
	for _, status := range []int{400, 401, 403, 404, 409, 503} {
		t.Run(fmt.Sprint(status), func(t *testing.T) {
			col := parityClient(t, http.MethodPost, webhookPath, "", `{"url":"https://receiver.example/events"}`, `{"error":{"code":"invalid_request","message":"rejected"}}`, status).Database(parityProject, "main").Collection("users")
			_, err := col.CreateWebhook(t.Context(), "https://receiver.example/events")
			var api *tb.APIError
			if !errors.As(err, &api) || api.Status != status {
				t.Fatalf("HTTP error was lost: %v", err)
			}
		})
	}
	for _, reply := range []string{`null`, `[{}]`, `[{"id":"not-ulid"}]`, `{"items":[]}`} {
		t.Run(reply, func(t *testing.T) {
			col := parityClient(t, http.MethodGet, webhookPath, "", "", reply, http.StatusOK).Database(parityProject, "main").Collection("users")
			if _, err := col.Webhooks(t.Context(), tb.WebhookListOptions{}); err == nil {
				t.Fatal("accepted malformed webhook list")
			}
		})
	}
	t.Run("empty final page", func(t *testing.T) {
		col := parityClient(t, http.MethodGet, webhookPath, "", "", `[]`, http.StatusOK).Database(parityProject, "main").Collection("users")
		out, err := col.Webhooks(t.Context(), tb.WebhookListOptions{})
		if err != nil || out == nil || len(out) != 0 {
			t.Fatalf("empty page failed: %v", err)
		}
	})
}
