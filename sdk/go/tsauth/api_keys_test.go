package tsauth

import (
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestRotateAPIKeyRoundTrip(t *testing.T) {
	t.Parallel()
	const org, project, key = "01ARZ3NDEKTSV4RRFFQ69G5FA3", "01ARZ3NDEKTSV4RRFFQ69G5FA6", "01ARZ3NDEKTSV4RRFFQ69G5FA9"
	var method, path, body string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		method, path = r.Method, r.URL.Path
		buf, readErr := io.ReadAll(r.Body)
		if readErr != nil {
			t.Errorf("read body: %v", readErr)
		}
		body = string(buf)
		if r.Header.Get("Authorization") != "Bearer fixture-sign-in" {
			t.Errorf("authorization = %q", r.Header.Get("Authorization"))
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, `{"api_key":{"id":"`+key+`","name":"backend","organization_id":"`+org+`","project_id":"`+project+
			`","scopes":["sql:read"],"created_by":"`+org+`","created_at":"2026-01-01T00:00:00Z","expires_at":null,`+
			`"last_used_at":null,"previous_secret_expires_at":"2026-01-02T00:00:00Z","allowed_cidrs":[]},"key":"tb_fixture-secret"}`)
	}))
	defer server.Close()

	client, err := New(Config{Issuer: server.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	rotated, err := client.WithToken("fixture-sign-in").RotateAPIKey(t.Context(), org, project, key, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	if method != http.MethodPost || path != "/v1/terrascale/organizations/"+org+"/projects/"+project+"/api-keys/"+key+"/rotate" {
		t.Errorf("request = %s %s", method, path)
	}
	if body != `{"previous_secret_ttl":3600}` {
		t.Errorf("body = %q", body)
	}
	if rotated.APIKey.ID != key || rotated.Key != "tb_fixture-secret" {
		t.Fatalf("rotated = %+v", rotated)
	}
	if _, err := client.WithToken("fixture-sign-in").RotateAPIKey(t.Context(), org, project, key, 25*time.Hour); err == nil ||
		!strings.Contains(err.Error(), "grace period") {
		t.Fatalf("out-of-range grace error = %v", err)
	}
}
