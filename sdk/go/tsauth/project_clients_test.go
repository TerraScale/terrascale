package tsauth

import (
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestRotateProjectOAuthClientSecretRoundTrip(t *testing.T) {
	t.Parallel()
	const project, client = "01ARZ3NDEKTSV4RRFFQ69G5FA6", "01ARZ3NDEKTSV4RRFFQ69G5FA9"
	var method, path, body, proof string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		method, path, proof = r.Method, r.URL.Path, r.Header.Get(StepUpProofHeader)
		buf, readErr := io.ReadAll(r.Body)
		if readErr != nil {
			t.Errorf("read body: %v", readErr)
		}
		body = string(buf)
		if r.Header.Get("Authorization") != "Bearer fixture-sign-in" {
			t.Errorf("authorization = %q", r.Header.Get("Authorization"))
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"client_id":"` + client + `","client_secret":"fixture-client-secret","previous_secret_expires_at":"2026-10-14T00:00:00Z"}`))
	}))
	defer server.Close()

	client_, err := New(Config{Issuer: server.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	grace := 3600
	u := client_.WithToken("fixture-sign-in").WithStepUpProof("fixture-proof")
	secret, err := u.RotateProjectOAuthClientSecret(t.Context(), project, client, ProjectClientRotation{GraceSeconds: &grace})
	if err != nil {
		t.Fatal(err)
	}
	if secret.ClientID != client || secret.ClientSecret != "fixture-client-secret" || secret.PreviousSecretExpiresAt.IsZero() {
		t.Fatalf("secret = %+v", secret)
	}
	if secret.String() != "ProjectClientSecret{secret:[REDACTED]}" {
		t.Errorf("String() = %q", secret.String())
	}
	if method != http.MethodPost || path != "/v1/"+project+"/clients/"+client+"/secret/rotate" {
		t.Errorf("request = %s %s", method, path)
	}
	if proof != "fixture-proof" {
		t.Errorf("step-up proof = %q", proof)
	}
	if !strings.Contains(body, `"grace_seconds":3600`) {
		t.Errorf("body = %q", body)
	}
}
