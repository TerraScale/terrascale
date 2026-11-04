package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/url"
	"strings"
	"testing"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

type branchTransport func(*http.Request) (*http.Response, error)

func (f branchTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

const branchProject = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
const branchSource = "01ARZ3NDEKTSV4RRFFQ69G5FAW"
const branchTarget = "01ARZ3NDEKTSV4RRFFQ69G5FAX"

func TestBranchLifecycle(t *testing.T) {
	t.Parallel()
	source := v1.ManagedDatabase{ID: branchSource, ProjectID: branchProject, Name: "app"}
	expires := time.Now().Add(7 * 24 * time.Hour)
	branch := v1.ManagedDatabase{ID: branchTarget, ProjectID: branchProject, Name: "preview", BranchedFrom: &source.ID, ExpiresAt: &expires}
	created, deleted, scoped := false, false, false
	hc := &http.Client{Transport: branchTransport(func(r *http.Request) (*http.Response, error) {
		status, body := 200, any(nil)
		switch {
		case r.URL.Path == "/terrascale/oauth/token":
			body = map[string]any{"access_token": "header.project.signature", "token_type": "Bearer", "expires_in": 3600}
		case r.URL.Path == "/v1/terrascale/api-keys/self/scoped-keys":
			var in v1.ScopedKeyRequest
			if err := json.NewDecoder(r.Body).Decode(&in); err != nil || in.Database != branchTarget || in.Project != branchProject || in.Access != v1.ScopedKeyReadWrite || in.TTLSeconds == nil || *in.TTLSeconds > previewTTL || *in.TTLSeconds < previewTTL-60 {
				t.Errorf("unexpected scoped key request: %+v, %v", in, err)
			}
			scoped, status = true, 201
			body = v1.ScopedKey{ID: branchSource, Key: "fixture-secret", ProjectID: branchProject, Database: branchTarget, Access: v1.ScopedKeyReadWrite, ExpiresAt: expires}
		case r.Method == "GET" && strings.HasSuffix(r.URL.Path, "/databases/app"):
			body = source
		case r.Method == "POST" && strings.HasSuffix(r.URL.Path, "/app/branches/if-id/"+branchSource):
			var in struct {
				Name       string
				TTLSeconds int64
			}
			if err := json.NewDecoder(r.Body).Decode(&in); err != nil || in.Name != "preview" || in.TTLSeconds != previewTTL {
				t.Errorf("unexpected guarded fork body: %+v, %v", in, err)
			}
			created, status, body = true, 201, branch
		case r.Method == "GET" && strings.HasSuffix(r.URL.Path, "/databases"):
			body = map[string]any{"items": []v1.ManagedDatabase{source, branch}, "next": ""}
		case r.Method == "GET" && strings.HasSuffix(r.URL.Path, "/databases/preview"):
			body = branch
		case r.Method == "DELETE" && strings.HasSuffix(r.URL.Path, "/preview/if-id/"+branchTarget):
			deleted, status = true, 204
		default:
			t.Errorf("unexpected request %s %s", r.Method, r.URL)
			status = 500
		}
		data, _ := json.Marshal(body)
		return &http.Response{StatusCode: status, Header: http.Header{"Content-Type": {"application/json"}}, Body: io.NopCloser(bytes.NewReader(data)), Request: r}, nil
	})}
	client, err := v1.New(v1.Config{APIKey: branchSource + "_" + strings.Repeat("a", 52), HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	s := &session{tb: client, project: branchProject}
	var buf bytes.Buffer
	out := &jsonOutput{Writer: &buf}
	for _, command := range []string{"create", "list", "url", "delete"} {
		buf.Reset()
		args := []string{"preview"}
		if command == "list" {
			args = nil
		}
		if err := runBranch(t.Context(), s, "db branch "+command, args, "app", previewTTL, "", out); err != nil {
			t.Fatal(err)
		}
		switch command {
		case "list":
			var items []v1.ManagedDatabase
			if err := json.Unmarshal(buf.Bytes(), &items); err != nil || len(items) != 1 || items[0].ID != branchTarget {
				t.Fatalf("branch list: %s, %v", &buf, err)
			}
		case "url":
			var result struct{ ConnectionURL string }
			if err := json.Unmarshal(buf.Bytes(), &result); err != nil {
				t.Fatal(err)
			}
			u, err := url.Parse(result.ConnectionURL)
			if err != nil {
				t.Fatal(err)
			}
			password, _ := u.User.Password()
			if u.Host != "pg.terrascale.tech:5432" || u.Path != "/"+branchProject+"/"+branchTarget || password != "fixture-secret" || u.Query().Get("sslmode") != "verify-full" {
				t.Fatal("incorrect scoped connection URL")
			}
		}
	}
	if !created || !deleted || !scoped {
		t.Fatal("lifecycle did not complete")
	}
}

func TestBranchDeleteReplacementFailsClosed(t *testing.T) {
	t.Parallel()
	deletes := 0
	source := branchSource
	s := jsonDatabaseSession(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.Method == "GET" {
			_ = json.NewEncoder(w).Encode(v1.ManagedDatabase{ID: branchTarget, ProjectID: jsonAuthID, Name: "preview", BranchedFrom: &source})
			return
		}
		deletes++
		if r.Method != "DELETE" || !strings.HasSuffix(r.URL.Path, "/preview/if-id/"+branchTarget) {
			t.Error("unguarded deletion")
		}
		w.WriteHeader(409)
		_, _ = io.WriteString(w, `{"code":"database_identity_mismatch","message":"replaced"}`)
	})
	err := runBranch(t.Context(), s, "db branch delete", []string{"preview"}, "", 0, "", io.Discard)
	var api *v1.APIError
	if !errors.As(err, &api) || api.Status != 409 || deletes != 1 {
		t.Fatalf("replacement guard: %v, deletes=%d", err, deletes)
	}
	// Invalid input and unattended deletion must stop before opening credentials.
	for _, args := range [][]string{
		{"db", "branch", "create", "preview"},
		{"db", "branch", "create", "preview", "--from", "app", "--ttl", "0d"},
		{"db", "branch", "create", "app", "--from", "app"},
		{"db", "branch", "list", "--ttl", "7d"},
	} {
		if err := run(t.Context(), args, io.Discard); !errors.Is(err, errUsage) {
			t.Fatalf("validation %v: %v", args, err)
		}
	}
	if err := runWithInput(t.Context(), []string{"db", "branch", "delete", "preview"}, io.Discard, strings.NewReader(""), io.Discard, false); err == nil {
		t.Fatal("missing deletion consent")
	}
}
