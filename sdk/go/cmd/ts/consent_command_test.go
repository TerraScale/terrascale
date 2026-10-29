package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

// Both authentication routes converge on dispatch with the actual command input.
func TestConsentDispatchCredentialPaths(t *testing.T) {
	t.Parallel()
	for _, credential := range []string{"key", "user"} {
		for _, command := range []string{"collection drop", "functions delete", "import"} {
			for _, consent := range []string{"denied", "unattended", "yes"} {
				t.Run(credential+"/"+command+"/"+consent, func(t *testing.T) {
					t.Parallel()
					var mutations, tokens atomic.Int32
					job := v1.ImportJob{ID: managementProjectID, Project: managementProjectID, Database: "app", State: "ready", Source: v1.ImportSource{ReceivedBytes: 3}, Preview: &v1.ImportPreview{ID: managementProjectID}, Progress: &v1.ImportProgress{RowsWritten: 1}}
					srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
						w.Header().Set("Content-Type", "application/json")
						if r.URL.Path == "/terrascale/oauth/token" {
							tokens.Add(1)
							r.ParseForm()
							want := "client_credentials"
							if credential == "user" {
								want = "urn:ietf:params:oauth:grant-type:token-exchange"
							}
							if r.Method != "POST" || r.Form.Get("grant_type") != want {
								t.Error("wrong authentication grant")
							}
							io.WriteString(w, `{"access_token":"fixture.project.signature","token_type":"Bearer","expires_in":900}`)
							return
						}
						if r.Header.Get("Authorization") != "Bearer fixture.project.signature" {
							t.Error("missing project authorization")
						}
						switch {
						case r.Method == "DELETE":
							mutations.Add(1)
							if command == "functions delete" {
								w.WriteHeader(http.StatusNoContent)
							} else {
								io.WriteString(w, `{"id":"`+managementProjectID+`","name":"people","created_at":"1"}`)
							}
						case strings.HasSuffix(r.URL.Path, "/import/start"):
							var req struct {
								PreviewID string `json:"previewId"`
							}
							if err := json.NewDecoder(r.Body).Decode(&req); err != nil || req.PreviewID != managementProjectID {
								t.Error("wrong confirmed preview")
							}
							mutations.Add(1)
							json.NewEncoder(w).Encode(job)
						case strings.HasSuffix(r.URL.Path, "/import"):
							result := job
							if r.Method == "GET" {
								result.State = "completed"
							}
							json.NewEncoder(w).Encode(result)
						default:
							if r.Method != "POST" || !strings.HasSuffix(r.URL.Path, "/databases") {
								t.Error("unexpected staging request")
							}
							w.WriteHeader(http.StatusConflict)
							io.WriteString(w, `{"error":{"code":"name_taken","message":"exists"}}`)
						}
					}))
					t.Cleanup(srv.Close)
					var source *tsauth.TokenSource
					var err error
					if credential == "key" {
						source, err = tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Issuer: srv.URL + "/terrascale", Key: managementProjectID + "_" + strings.Repeat("0", 52), Project: managementProjectID})
					} else {
						source, err = tsauth.NewExchangeSource(tsauth.ExchangeConfig{Issuer: srv.URL + "/terrascale", ClientID: tsauth.CLIClientID, Organization: managementOrgID, Project: managementProjectID, SubjectToken: func(context.Context) (string, error) { return "fixture.user.signature", nil }})
					}
					if err != nil {
						t.Fatal(err)
					}
					client, err := v1.New(v1.Config{BaseURL: srv.URL, TokenSource: source})
					if err != nil {
						t.Fatal(err)
					}
					args := []string{"app", "people"}
					if command == "import" {
						path := filepath.Join(t.TempDir(), "source.ndjson")
						if err := os.WriteFile(path, []byte("{}\n"), 0600); err != nil {
							t.Fatal(err)
						}
						args = []string{"app", "ndjson", path}
					}
					var stdout, prompt bytes.Buffer
					out := &jsonOutput{Writer: &stdout, progress: &prompt}
					input := commandInput{in: strings.NewReader("wrong\n"), prompt: &prompt, tty: consent == "denied", yes: consent == "yes"}
					err = (&session{tb: client, project: managementProjectID}).dispatch(t.Context(), command, functionFlags{}, "people", args, input, out)
					if consent == "yes" {
						if err != nil || mutations.Load() != 1 || tokens.Load() != 1 {
							t.Fatalf("consented mutation=%d tokens=%d error=%v", mutations.Load(), tokens.Load(), err)
						}
					} else {
						if err == nil || mutations.Load() != 0 {
							t.Fatalf("denied mutation=%d error=%v", mutations.Load(), err)
						}
						if consent == "denied" && !errors.Is(err, errUsage) {
							t.Fatalf("typed refusal=%v", err)
						}
						if stdout.Len() != 0 {
							t.Fatal("prose leaked to JSON stdout")
						}
						if err := emitJSONError(out, err); err != nil {
							t.Fatal(err)
						}
					}
					if !json.Valid(stdout.Bytes()) {
						t.Fatalf("invalid JSON: %s", stdout.String())
					}
				})
			}
		}
	}
}

func TestConsentCommandsRefuseBeforeAuthentication(t *testing.T) {
	t.Parallel()
	for _, command := range [][]string{{"collection", "drop", "app", "people"}, {"functions", "delete", "app", "handler"}} {
		var stdout bytes.Buffer
		err := runWithInput(t.Context(), append([]string{"--json"}, command...), &stdout, forbiddenInput{}, io.Discard, false)
		if err == nil || errors.Is(err, errUsage) || !json.Valid(stdout.Bytes()) || !strings.Contains(stdout.String(), `"error"`) {
			t.Fatalf("offline refusal=%v JSON=%s", err, stdout.String())
		}
	}
}
