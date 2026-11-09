package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

const initKeyID = "01M5N9000000000000000000AC"

func initSession(t *testing.T) *session {
	t.Helper()
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch {
		case r.Method == "POST" && r.URL.Path == "/v1/terrascale/step-up":
			_ = json.NewEncoder(w).Encode(tsauth.StepUpBody{Proof: initKeyID + ".fixture-proof",
				StepUpURL: "http://" + r.Host + "/ui/terrascale/step-up/" + initKeyID, ExpiresAt: time.Now().Add(5 * time.Minute)})
		case r.Method == "POST" && strings.HasSuffix(r.URL.Path, "/api-keys"):
			var req tsauth.APIKeyRequest
			_ = json.NewDecoder(r.Body).Decode(&req)
			if req.Name == "" || !strings.HasPrefix(req.Name, "ts-init-main-") ||
				req.Database != jsonAuthID || len(req.Scopes) != 2 {
				t.Errorf("init key request: %+v", req)
			}
			key := map[string]any{"id": initKeyID, "name": req.Name, "organization_id": jsonAuthID, "project_id": jsonAuthID,
				"database": jsonAuthID, "scopes": req.Scopes, "created_by": jsonAuthID, "created_at": "2026-01-01T00:00:00Z",
				"expires_at": nil, "previous_secret_expires_at": nil, "last_used_at": nil, "allowed_cidrs": []string{}}
			w.WriteHeader(http.StatusCreated)
			_ = json.NewEncoder(w).Encode(map[string]any{"api_key": key, "key": initKeyID + "_fixture-secret-0000000000000000000000000000000000000000000000"})
		case r.Method == "GET" && strings.HasSuffix(r.URL.Path, "/databases"):
			_ = json.NewEncoder(w).Encode(map[string]any{"items": []any{}, "next": ""})
		case r.Method == "POST" && strings.HasSuffix(r.URL.Path, "/databases"):
			_ = json.NewEncoder(w).Encode(map[string]any{"id": jsonAuthID, "projectId": jsonAuthID, "name": "main"})
		default:
			t.Errorf("unexpected request: %s %s", r.Method, r.URL.Path)
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	t.Cleanup(server.Close)
	auth, err := tsauth.New(tsauth.Config{Issuer: server.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	tb, err := v1.New(v1.Config{BaseURL: server.URL, Token: "fixture.jwt.signature"})
	if err != nil {
		t.Fatal(err)
	}
	return &session{tb: tb, user: auth.WithToken("header.payload.signature"), org: jsonAuthID, project: jsonAuthID}
}

func TestInitCreatesDatabaseKeyAndEnv(t *testing.T) {
	dir := t.TempDir()
	t.Chdir(dir)
	if err := os.WriteFile(filepath.Join(dir, "go.mod"), []byte("module example.com/quickstart\n\ngo 1.25\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	s := initSession(t)
	var out, prompt bytes.Buffer
	input := commandInput{in: strings.NewReader("\n"), prompt: &prompt, tty: false, yes: true}
	if err := runInit(t.Context(), s, "", "", input, &out, &prompt); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(filepath.Join(dir, ".env"))
	if err != nil {
		t.Fatal(err)
	}
	env := string(data)
	if !strings.Contains(env, "TERRASCALE_PROJECT_ID="+jsonAuthID+"\n") ||
		!strings.Contains(env, "TERRASCALE_DATABASE_NAME=main\n") ||
		!strings.Contains(env, "TERRASCALE_API_KEY="+initKeyID+"_fixture-secret") {
		t.Fatalf(".env = %q", env)
	}
	if info, err := os.Stat(filepath.Join(dir, ".env")); err != nil || info.Mode().Perm() != 0o600 {
		t.Fatalf(".env permissions: %v, %v", info, err)
	}
	if !strings.Contains(out.String(), "Created database main") || !strings.Contains(out.String(), "go get terrascale.tech/sdk") ||
		!strings.Contains(out.String(), "tsauth.NewAPIKeySource") || strings.Contains(out.String(), "npm install") {
		t.Fatalf("init output: %q", out.String())
	}
}
