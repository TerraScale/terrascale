package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/sdk/tsauth"
)

const stepUpKeyID = "01M5N9000000000000000000AB"

// stepUpSession serves the auth API from one httptest server: the step-up
// request, its pending refusals and the proof-carrying key creation. The first
// approveAfter proof-carrying attempts still answer step_up_required: the
// approval has not landed yet.
func stepUpSession(t *testing.T, refusals *atomic.Int32, approveAfter int32, expiry time.Duration) *session {
	t.Helper()
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch {
		case r.Method == "POST" && r.URL.Path == "/v1/terrascale/step-up":
			var request tsauth.StepUpRequest
			if err := json.NewDecoder(r.Body).Decode(&request); err != nil || request.Purpose != tsauth.StepUpCreateAPIKey ||
				request.Audience != "ts-auth" || request.ReturnURI != "https://dash.terrascale.tech/auth/step-up/callback" {
				t.Errorf("step-up request: %+v, %v", request, err)
			}
			_ = json.NewEncoder(w).Encode(tsauth.StepUpBody{Proof: stepUpKeyID + ".fixture-proof",
				StepUpURL: "http://" + r.Host + "/ui/terrascale/step-up/" + stepUpKeyID, ExpiresAt: time.Now().Add(expiry)})
		case r.Method == "POST" && strings.HasSuffix(r.URL.Path, "/api-keys"):
			if r.Header.Get(tsauth.StepUpProofHeader) != stepUpKeyID+".fixture-proof" {
				t.Errorf("key creation without its proof")
			}
			if refusals.Add(1) <= approveAfter {
				w.WriteHeader(http.StatusForbidden)
				_, _ = io.WriteString(w, `{"error":{"code":"step_up_required","message":"pending"}}`)
				return
			}
			key := map[string]any{"id": stepUpKeyID, "name": "backend", "organization_id": jsonAuthID, "project_id": jsonAuthID,
				"scopes": []string{"sql:read"}, "created_by": jsonAuthID, "created_at": "2026-01-01T00:00:00Z",
				"expires_at": nil, "previous_secret_expires_at": nil, "last_used_at": nil, "allowed_cidrs": []string{}}
			w.WriteHeader(http.StatusCreated)
			_ = json.NewEncoder(w).Encode(map[string]any{"api_key": key, "key": stepUpKeyID + "_fixture-secret-0000000000000000000000000000000000000000000000"})
		default:
			t.Errorf("unexpected request: %s %s", r.Method, r.URL.Path)
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	t.Cleanup(server.Close)
	client, err := tsauth.New(tsauth.Config{Issuer: server.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	return &session{user: client.WithToken("header.payload.signature"), org: jsonAuthID, project: jsonAuthID}
}

func TestCreateKeyCompletesStepUp(t *testing.T) {
	previous := stepUpPollInterval
	stepUpPollInterval = 10 * time.Millisecond
	t.Cleanup(func() { stepUpPollInterval = previous })
	var refusals atomic.Int32
	s := stepUpSession(t, &refusals, 1, 5*time.Minute)
	var diagnostics, out bytes.Buffer
	if err := createKey(t.Context(), s, tsauth.APIKeyRequest{Name: "backend"}, &out, &diagnostics); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), stepUpKeyID+"_fixture-secret") {
		t.Fatalf("secret not printed once: %q", out.String())
	}
	if !strings.Contains(diagnostics.String(), "/ui/terrascale/step-up/"+stepUpKeyID) {
		t.Fatalf("approval URL not announced: %q", diagnostics.String())
	}
	if refusals.Load() != 2 {
		t.Fatalf("attempts = %d", refusals.Load())
	}
}

func TestCreateKeyStepUpDeadline(t *testing.T) {
	previous := stepUpPollInterval
	stepUpPollInterval = 10 * time.Millisecond
	t.Cleanup(func() { stepUpPollInterval = previous })
	var refusals atomic.Int32
	s := stepUpSession(t, &refusals, 1<<20, 50*time.Millisecond)
	var diagnostics, out bytes.Buffer
	err := createKey(t.Context(), s, tsauth.APIKeyRequest{Name: "backend"}, &out, &diagnostics)
	var cli *cliError
	if err == nil || !errors.As(err, &cli) || cli.code != "step_up_required" || out.Len() != 0 {
		t.Fatalf("deadline error = %v, out = %q", err, out.String())
	}
}
