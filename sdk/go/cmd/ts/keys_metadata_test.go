package main

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/sdk/tsauth"
)

func TestKeyMetadataPagesAndNullableLastUsed(t *testing.T) {
	t.Parallel()
	for _, asJSON := range []bool{false, true} {
		t.Run(map[bool]string{false: "text", true: "json"}[asJSON], func(t *testing.T) {
			t.Parallel()
			var calls atomic.Int32
			u := jsonAuthUser(t, func(w http.ResponseWriter, r *http.Request) {
				calls.Add(1)
				if r.Method != "GET" || !strings.HasSuffix(r.URL.Path, "/api-keys") || r.URL.Query().Get("limit") != "100" {
					t.Error("unexpected key listing request")
				}
				var used any
				next := "second-page"
				if r.URL.Query().Get("after") != "" {
					used, next = "2026-10-26T12:30:00Z", ""
				}
				// Unknown secret-bearing fields must never escape the typed SDK boundary.
				json.NewEncoder(w).Encode(map[string]any{"items": []any{map[string]any{"id": jsonAuthID,
					"organization_id": jsonAuthID, "project_id": jsonAuthID, "created_by": jsonAuthID,
					"name": "reports", "database": jsonAuthID, "scopes": []string{tsauth.ScopeDatabasesRead, tsauth.ScopeDocumentsRead},
					"allowed_cidrs": []string{}, "expires_at": nil, "previous_secret_expires_at": nil,
					"created_at": "2026-10-25T00:00:00Z", "last_used_at": used, "key": "do-not-log-fixture"}}, "next": next})
			})
			var stdout bytes.Buffer
			var out io.Writer = &stdout
			if asJSON {
				out = &jsonOutput{Writer: out}
			}
			err := listKeyMetadata(t.Context(), &session{user: u, org: jsonAuthID, project: jsonAuthID}, out)
			if err != nil || calls.Load() != 2 || strings.Contains(stdout.String(), "do-not-log-fixture") {
				t.Fatal("metadata listing must page without leaking secret fields")
			}
			if asJSON {
				var keys []tsauth.APIKey
				if json.Unmarshal(stdout.Bytes(), &keys) != nil || len(keys) != 2 || keys[0].LastUsedAt != nil ||
					keys[1].LastUsedAt == nil || !keys[1].LastUsedAt.Equal(time.Date(2026, 10, 26, 12, 30, 0, 0, time.UTC)) ||
					!strings.Contains(stdout.String(), `"created_by"`) || !strings.Contains(stdout.String(), `"created_at"`) || !strings.Contains(stdout.String(), `"last_used_at":null`) {
					t.Fatal("typed list must retain exact metadata names and nullable timestamps")
				}
			} else if !strings.Contains(stdout.String(), "CREATED BY") || !strings.Contains(stdout.String(), "LAST USED") ||
				!strings.Contains(stdout.String(), "2026-10-25T00:00:00Z") || !strings.Contains(stdout.String(), "2026-10-26T12:30:00Z") || !strings.Contains(stdout.String(), "never") {
				t.Fatal("text list must include creator, creation time and nullable last use")
			}
		})
	}
}

func TestKeyMetadataFailureHasNoPartialListOrRemoteContent(t *testing.T) {
	t.Parallel()
	u := jsonAuthUser(t, func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Query().Get("after") == "" {
			io.WriteString(w, `{"items":[],"next":"second-page"}`)
		} else {
			w.WriteHeader(http.StatusForbidden)
			io.WriteString(w, `{"error":{"code":"forbidden_role","message":"do-not-log-fixture"}}`)
		}
	})
	var stdout bytes.Buffer
	err := listKeyMetadata(t.Context(), &session{user: u, org: jsonAuthID, project: jsonAuthID}, &stdout)
	if err == nil || stdout.Len() != 0 || strings.Contains(err.Error(), "do-not-log-fixture") {
		t.Fatal("failed later pages must not leave a successful-looking list")
	}
}

func TestKeyMetadataEmptyAndWriterFailure(t *testing.T) {
	t.Parallel()
	u := jsonAuthUser(t, func(w http.ResponseWriter, r *http.Request) { io.WriteString(w, `{"items":[],"next":null}`) })
	s := &session{user: u, org: jsonAuthID, project: jsonAuthID}
	var stdout bytes.Buffer
	if err := listKeyMetadata(t.Context(), s, &jsonOutput{Writer: &stdout}); err != nil || stdout.String() != "[]\n" {
		t.Fatal("empty list must be a JSON array")
	}
	if err := listKeyMetadata(t.Context(), s, keyRejectWriter{}); err == nil || strings.Contains(err.Error(), "do-not-log-fixture") {
		t.Fatal("metadata writer failures must be reported without rejected content")
	}
}
