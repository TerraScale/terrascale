package tsauth_test

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"testing"
	"time"

	"terrascale.tech/sdk/tsauth"
)

const managementID = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
const managementProjectPath = "/v1/terrascale/organizations/" + managementID + "/projects/" + managementID
const managementProjectJSON = `{"id":"` + managementID + `","organization_id":"` + managementID + `","created_by":"` + managementID + `","name":"test","created_at":"2026-10-26T04:00:00Z","role":"admin","allowed_cidrs":%s}`

func managementPeer(t *testing.T, h http.HandlerFunc) *tsauth.User {
	t.Helper()
	peer := httptest.NewServer(h)
	t.Cleanup(peer.Close)
	client, err := tsauth.New(tsauth.Config{Issuer: peer.URL + "/terrascale", HTTPClient: peer.Client()})
	if err != nil {
		t.Fatal(err)
	}
	return client.WithToken("test-access-token")
}

func TestProjectDeletionOutcomes(t *testing.T) {
	for _, tc := range []struct {
		status int
		code   string
		want   tsauth.ProjectDeletionResult
	}{
		{202, "", tsauth.ProjectDeletionPending}, {204, "", tsauth.ProjectDeletionCompleted},
		{409, "not_empty", 0}, {409, "closure_conflict", 0}, {409, "binding_conflict", 0},
		{200, "", 0},
	} {
		t.Run(fmt.Sprint(tc.status, tc.code), func(t *testing.T) {
			calls := 0
			u := managementPeer(t, func(w http.ResponseWriter, r *http.Request) {
				calls++
				if r.Method != http.MethodDelete || r.URL.Path != managementProjectPath || r.Header.Get("Authorization") != "Bearer test-access-token" {
					t.Error("incorrect deletion request")
				}
				w.WriteHeader(tc.status)
				if tc.code != "" {
					fmt.Fprintf(w, `{"error":{"code":%q,"message":"conflict"}}`, tc.code)
				}
			})
			got, err := u.DeleteProject(t.Context(), managementID, managementID)
			if got != tc.want || (err != nil) != (tc.want == 0) || calls != 1 {
				t.Fatalf("outcome=%v err=%v calls=%d", got, err, calls)
			}
			if tc.code != "" {
				var api *tsauth.APIError
				if !errors.As(err, &api) || api.Status != tc.status || !errors.Is(err, tsauth.ErrorCode(tc.code)) {
					t.Fatalf("lost typed conflict: %v", err)
				}
			}
		})
	}
}

func TestManagementCancellationAndPathValidation(t *testing.T) {
	calls := 0
	u := managementPeer(t, func(w http.ResponseWriter, r *http.Request) { calls++; w.WriteHeader(204) })
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if result, err := u.DeleteProject(ctx, managementID, managementID); result != 0 || !errors.Is(err, context.Canceled) {
		t.Fatalf("canceled deletion: %v, %v", result, err)
	}
	if _, err := u.SetProjectAllowedCIDRs(ctx, managementID, managementID, []string{}); !errors.Is(err, context.Canceled) {
		t.Fatalf("canceled CIDR update: %v", err)
	}
	for _, bad := range []string{"", "..", managementID + "/members", "legacy"} {
		if _, err := u.SetProjectAllowedCIDRs(t.Context(), managementID, bad, []string{}); err == nil {
			t.Fatal("invalid project path accepted")
		}
	}
	if calls != 0 {
		t.Fatalf("invalid or canceled requests reached server: %d", calls)
	}
}

func TestProjectCIDRReplacement(t *testing.T) {
	for _, tc := range []struct {
		name   string
		cidrs  []string
		status int
		code   string
	}{
		{"networks", []string{"203.0.113.0/24", "2001:db8::/32"}, 200, ""},
		{"clear", []string{}, 200, ""}, {"null", nil, 400, "invalid_request"},
		{"invalid", []string{"bad"}, 400, "invalid_request"},
		{"too-many", slices.Repeat([]string{"203.0.113.1"}, 33), 400, "invalid_request"},
		{"role", []string{}, 403, "forbidden_role"}, {"auth", []string{}, 401, "unauthorized"},
		{"missing", []string{}, 404, "not_found"}, {"unavailable", []string{}, 503, "temporarily_unavailable"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			calls := 0
			encoded, _ := json.Marshal(tc.cidrs)
			u := managementPeer(t, func(w http.ResponseWriter, r *http.Request) {
				calls++
				body, _ := io.ReadAll(r.Body)
				if r.Method != http.MethodPut || r.URL.Path != managementProjectPath+"/allowed-cidrs" || string(body) != `{"allowed_cidrs":`+string(encoded)+`}` || r.Header.Get("Content-Type") != "application/json" {
					t.Error("CIDR request changed path, media type or null/empty semantics")
				}
				w.WriteHeader(tc.status)
				if tc.code != "" {
					fmt.Fprintf(w, `{"error":{"code":%q}}`, tc.code)
				} else {
					fmt.Fprintf(w, managementProjectJSON, encoded)
				}
			})
			got, err := u.SetProjectAllowedCIDRs(t.Context(), managementID, managementID, tc.cidrs)
			if calls != 1 || (tc.code != "" && (!errors.Is(err, tsauth.ErrorCode(tc.code)) || got != nil)) || (tc.code == "" && (err != nil || got == nil || !slices.Equal(got.AllowedCIDRs, tc.cidrs) || got.AllowedCIDRs == nil)) {
				t.Fatalf("project=%+v err=%v calls=%d", got, err, calls)
			}
		})
	}
}

func TestProjectCIDRReadback(t *testing.T) {
	for _, cidrs := range []string{`[]`, `["203.0.113.0/24","2001:db8::/32"]`} {
		t.Run(cidrs, func(t *testing.T) {
			body := fmt.Sprintf(managementProjectJSON, cidrs)
			u := managementPeer(t, func(w http.ResponseWriter, r *http.Request) {
				if r.Method == http.MethodGet && strings.HasSuffix(r.URL.Path, "/projects") {
					fmt.Fprintf(w, `{"items":[%s],"next":null}`, body)
				} else {
					io.WriteString(w, body)
				}
			})
			created, e1 := u.CreateProject(t.Context(), managementID, "test")
			read, e2 := u.Project(t.Context(), managementID, managementID)
			renamed, e3 := u.RenameProject(t.Context(), managementID, managementID, "renamed")
			page, e4 := u.Projects(t.Context(), managementID, tsauth.PageOptions{})
			if err := errors.Join(e1, e2, e3, e4); err != nil {
				t.Fatal(err)
			}
			for _, project := range []*tsauth.Project{created, read, renamed, &page.Items[0]} {
				encoded, _ := json.Marshal(project.AllowedCIDRs)
				if string(encoded) != cidrs {
					t.Fatalf("lost CIDRs: %s", encoded)
				}
			}
		})
	}
}

func TestAPIKeyLastUsedMetadata(t *testing.T) {
	for _, used := range []string{`null`, `"2026-10-26T04:00:00.1234567+02:00"`} {
		for _, project := range []string{"", managementID} {
			t.Run(used+project, func(t *testing.T) {
				key := `{"id":"` + managementID + `","organization_id":"` + managementID + `","created_by":"` + managementID + `","name":"test","scopes":["documents:read"],"created_at":"2026-10-26T04:00:00Z","last_used_at":` + used + `}`
				u := managementPeer(t, func(w http.ResponseWriter, r *http.Request) {
					switch {
					case r.Method == http.MethodPost:
						fmt.Fprintf(w, `{"api_key":%s,"key":"test-only"}`, key)
					case strings.HasSuffix(r.URL.Path, "/api-keys"):
						fmt.Fprintf(w, `{"items":[%s]}`, key)
					default:
						io.WriteString(w, key)
					}
				})
				read, e1 := u.APIKey(t.Context(), managementID, project, managementID)
				page, e2 := u.APIKeys(t.Context(), managementID, project, tsauth.PageOptions{})
				created, e3 := u.CreateAPIKey(t.Context(), managementID, project, tsauth.APIKeyRequest{Name: "test", Scopes: []string{tsauth.ScopeDocumentsRead}})
				rotated, e4 := u.RotateAPIKey(t.Context(), managementID, project, managementID, time.Hour)
				if err := errors.Join(e1, e2, e3, e4); err != nil {
					t.Fatal(err)
				}
				for _, key := range []*tsauth.APIKey{read, &page.Items[0], &created.APIKey, &rotated.APIKey} {
					if used == "null" {
						if key.LastUsedAt != nil {
							t.Fatal("null timestamp became a value")
						}
					} else {
						want, _ := time.Parse(time.RFC3339Nano, strings.Trim(used, `"`))
						if key.LastUsedAt == nil || !key.LastUsedAt.Equal(want) {
							t.Fatal("lost last-used timestamp precision or offset")
						}
					}
				}
			})
		}
	}
}

func TestManagementMalformedResponseIsSafe(t *testing.T) {
	u := managementPeer(t, func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, `{"allowed_cidrs":"private-response-value"}`)
	})
	if _, err := u.SetProjectAllowedCIDRs(t.Context(), managementID, managementID, []string{}); err == nil || strings.Contains(err.Error(), "private-response-value") {
		t.Fatalf("unsafe or missing decode error: %v", err)
	}
}

// Cancellation while reading an accepted response must not certify completion
// or replay the mutation, and the SDK must release the response body.
type canceledManagementBody struct {
	ctx    context.Context
	closed bool
}

func (b *canceledManagementBody) Read([]byte) (int, error) { return 0, b.ctx.Err() }
func (b *canceledManagementBody) Close() error             { b.closed = true; return nil }

func TestProjectDeletionResponseCancellation(t *testing.T) {
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	body := &canceledManagementBody{ctx: ctx}
	calls := 0
	client, err := tsauth.New(tsauth.Config{Issuer: "https://auth.example/terrascale", HTTPClient: &http.Client{Transport: roundTripFunc(func(r *http.Request) (*http.Response, error) {
		calls++
		cancel()
		return &http.Response{StatusCode: 202, Header: make(http.Header), Body: body, Request: r}, nil
	})}})
	if err != nil {
		t.Fatal(err)
	}
	got, err := client.WithToken("test-access-token").DeleteProject(ctx, managementID, managementID)
	if got != 0 || !errors.Is(err, context.Canceled) || calls != 1 || !body.closed {
		t.Fatalf("outcome=%v err=%v calls=%d closed=%v", got, err, calls, body.closed)
	}
}

func TestAPIKeyDatabaseScopeUsesImmutableULID(t *testing.T) {
	u := managementPeer(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodPost {
			var body struct {
				Database string `json:"database"`
			}
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Database != managementID {
				t.Error("key must carry the database ULID")
			}
		}
		key := `{"id":"` + managementID + `","organization_id":"` + managementID + `","created_by":"` + managementID + `","database":"` + managementID + `","name":"test","scopes":["documents:read"],"created_at":"2026-10-26T04:00:00Z"}`
		if r.Method == http.MethodPost {
			fmt.Fprintf(w, `{"api_key":%s,"key":"test-only"}`, key)
		} else {
			fmt.Fprintf(w, `{"items":[%s]}`, key)
		}
	})
	key, err := u.CreateAPIKey(t.Context(), managementID, managementID, tsauth.APIKeyRequest{Name: "test", Database: managementID, Scopes: []string{tsauth.ScopeDocumentsRead}})
	if err != nil || key.APIKey.Database != managementID {
		t.Fatalf("create scope: %v", err)
	}
	page, err := u.APIKeys(t.Context(), managementID, managementID, tsauth.PageOptions{})
	if err != nil || len(page.Items) != 1 || page.Items[0].Database != managementID {
		t.Fatalf("list scope: %v", err)
	}
	u = managementPeer(t, func(w http.ResponseWriter, r *http.Request) { t.Error("invalid scope reached HTTP") })
	for _, database := range []string{"orders", "../escape", strings.ToLower(managementID), "81ARZ3NDEKTSV4RRFFQ69G5FAW"} {
		if _, err := u.CreateAPIKey(t.Context(), managementID, managementID, tsauth.APIKeyRequest{Name: "test", Database: database}); err == nil {
			t.Error("invalid database scope accepted")
		}
	}
}
