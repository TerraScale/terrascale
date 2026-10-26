package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/sdk/tsauth"
)

func TestKeyRotationGraceValidation(t *testing.T) {
	t.Parallel()
	for _, value := range []string{"0s", "1s", "30m", "1.5m", "24h"} {
		if _, _, err := parseKeyRotation([]string{jsonAuthID, value}); err != nil {
			t.Fatalf("valid duration rejected: %v", err)
		}
	}
	for _, args := range [][]string{{}, {jsonAuthID}, {"sensitive-operand", "0s"}, {jsonAuthID, "sensitive-operand"},
		{jsonAuthID, "-1s"}, {jsonAuthID, "0.5s"}, {jsonAuthID, "24h1s"}, {jsonAuthID, "86400"}} {
		_, _, err := parseKeyRotation(args)
		if !errors.Is(err, errUsage) || strings.Contains(err.Error(), "sensitive-operand") {
			t.Fatal("invalid operands must fail safely as usage errors")
		}
	}
}

func rotationFixture() tsauth.CreatedAPIKey {
	until := time.Date(2026, 10, 26, 12, 30, 0, 0, time.UTC)
	return tsauth.CreatedAPIKey{APIKey: tsauth.APIKey{ID: jsonAuthID, OrganizationID: jsonAuthID,
		ProjectID: jsonAuthID, CreatedBy: jsonAuthID, PreviousSecretExpiresAt: &until,
		Scopes: []string{tsauth.ScopeDatabasesRead, tsauth.ScopeDocumentsRead}, Database: "orders"}, Key: jsonAuthID + "_" + strings.Repeat("a", 52)}
}

func TestKeyRotationHTTPAndSecretOutput(t *testing.T) {
	t.Parallel()
	for _, seconds := range []int{0, 1800, 86400} {
		for _, asJSON := range []bool{false, true} {
			t.Run(fmt.Sprintf("%ds/json=%t", seconds, asJSON), func(t *testing.T) {
				t.Parallel()
				var calls atomic.Int32
				u := jsonAuthUser(t, func(w http.ResponseWriter, r *http.Request) {
					calls.Add(1)
					var body map[string]int
					if r.Method != "POST" || r.URL.Path != "/v1/terrascale/organizations/"+jsonAuthID+"/projects/"+jsonAuthID+"/api-keys/"+jsonAuthID+"/rotate" ||
						json.NewDecoder(r.Body).Decode(&body) != nil || len(body) != 1 || body["previous_secret_ttl"] != seconds {
						t.Error("rotation request contract mismatch")
					}
					key := rotationFixture()
					if seconds == 0 {
						key.APIKey.PreviousSecretExpiresAt = nil
					}
					json.NewEncoder(w).Encode(key)
				})
				var stdout, diagnostics bytes.Buffer
				var out io.Writer = &stdout
				if asJSON {
					out = &jsonOutput{Writer: out}
				}
				err := rotateKey(t.Context(), &session{user: u, org: jsonAuthID, project: jsonAuthID}, jsonAuthID, time.Duration(seconds)*time.Second, out, &diagnostics)
				if err != nil || calls.Load() != 1 || strings.Count(stdout.String(), rotationFixture().Key) != 1 || strings.Contains(diagnostics.String(), rotationFixture().Key) {
					t.Fatal("rotation must emit its secret once, only on stdout, without retries")
				}
				if asJSON {
					var result map[string]json.RawMessage
					if json.Unmarshal(stdout.Bytes(), &result) != nil || len(result) != 2 || result["api_key"] == nil || result["key"] == nil || diagnostics.Len() != 0 {
						t.Fatal("rotation must preserve the typed CreatedAPIKey contract")
					}
				}
			})
		}
	}
}

func TestKeyRotationFailuresStripRemoteContent(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"rotation_pending", "authentication_conflict", "forbidden_role", "not_found", "unknown-secret-code", "malformed", "wrong-id"} {
		t.Run(mode, func(t *testing.T) {
			t.Parallel()
			u := jsonAuthUser(t, func(w http.ResponseWriter, r *http.Request) {
				if mode == "malformed" {
					io.WriteString(w, `{"key":{"secret":"do-not-log-fixture"}}`)
				} else if mode == "wrong-id" {
					result := rotationFixture()
					result.APIKey.ProjectID = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
					json.NewEncoder(w).Encode(result)
				} else {
					status := http.StatusConflict
					if mode == "forbidden_role" {
						status = http.StatusForbidden
					}
					if mode == "not_found" {
						status = http.StatusNotFound
					}
					if mode == "unknown-secret-code" {
						status = http.StatusInternalServerError
					}
					w.WriteHeader(status)
					json.NewEncoder(w).Encode(map[string]any{"error": map[string]string{"code": mode, "message": "do-not-log-fixture"}})
				}
			})
			var stdout, diagnostics bytes.Buffer
			err := rotateKey(t.Context(), &session{user: u, org: jsonAuthID, project: jsonAuthID}, jsonAuthID, 0, &stdout, &diagnostics)
			if err == nil || stdout.Len() != 0 || diagnostics.Len() != 0 || strings.Contains(err.Error(), "do-not-log-fixture") || strings.Contains(err.Error(), "unknown-secret-code") {
				t.Fatal("failure must not expose remote values or an unvalidated secret")
			}
			if mode == "rotation_pending" && !errors.Is(err, tsauth.ErrorCode(mode)) {
				t.Fatal("rotation_pending identity must survive sanitization")
			}
		})
	}
}

type keyRejectWriter struct{}

func (keyRejectWriter) Write([]byte) (int, error) { return 0, errors.New("do-not-log-fixture") }

func TestKeyRotationWriterFailuresAreSafe(t *testing.T) {
	t.Parallel()
	u := jsonAuthUser(t, func(w http.ResponseWriter, r *http.Request) { json.NewEncoder(w).Encode(rotationFixture()) })
	for _, mode := range []string{"stdout", "diagnostics", "json"} {
		var out, diagnostics io.Writer = io.Discard, io.Discard
		if mode == "diagnostics" {
			diagnostics = keyRejectWriter{}
		} else {
			out = keyRejectWriter{}
			if mode == "json" {
				out = &jsonOutput{Writer: out}
			}
		}
		err := rotateKey(t.Context(), &session{user: u, org: jsonAuthID, project: jsonAuthID}, jsonAuthID, 30*time.Minute, out, diagnostics)
		if err == nil || strings.Contains(err.Error(), "do-not-log-fixture") {
			t.Fatal("writer failure must fail without leaking rejected bytes")
		}
	}
}

func TestKeyRotationCompanionCreateFailureBoundary(t *testing.T) {
	t.Parallel()
	u := jsonAuthUser(t, func(w http.ResponseWriter, r *http.Request) { json.NewEncoder(w).Encode(rotationFixture()) })
	out := &jsonOutput{Writer: keyRejectWriter{}}
	err := createKey(t.Context(), &session{user: u, org: jsonAuthID, project: jsonAuthID}, tsauth.APIKeyRequest{Name: "reports"}, out)
	if err == nil || strings.Contains(err.Error(), "do-not-log-fixture") || !out.failed {
		t.Fatal("creation output failures must be reported without rejected secret bytes")
	}
}

func TestKeyRotationScopedHelp(t *testing.T) {
	t.Parallel()
	var out bytes.Buffer
	handled, err := requestedHelp([]string{"keys", "rotate", "--help"}, &out)
	if !handled || err != nil || !strings.Contains(out.String(), "keys rotate <id> <grace>") || !strings.Contains(out.String(), "0s to 24h") {
		t.Fatal("offline scoped help must describe required grace and its bounds")
	}
}
