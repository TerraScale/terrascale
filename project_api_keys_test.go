package sdk

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"reflect"
	"testing"
)

func TestProjectAPIKeysUseDashboardLifecycleRoutes(t *testing.T) {
	var requests []string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer dashboard-session" {
			t.Errorf("authorization = %q", r.Header.Get("Authorization"))
		}
		requests = append(requests, r.Method+" "+r.URL.RequestURI())
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/v1/projects/project-1/auth/api-keys":
			if r.Method == http.MethodPost {
				var body CreateProjectAPIKeyRequest
				if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Name != "qualification" {
					t.Errorf("create body = %+v, error = %v", body, err)
				}
				_ = json.NewEncoder(w).Encode(ProjectAPIKeySecret{APIKey: ProjectAPIKey{ID: "key-1", ProjectID: "project-1"}, Secret: "ts_secret"})
				return
			}
			_ = json.NewEncoder(w).Encode(ProjectAPIKeyPage{APIKeys: []ProjectAPIKey{{ID: "key-1"}}, NextCursor: "next"})
		case "/api/v1/projects/project-1/auth/api-keys/key-1/rotate":
			_ = json.NewEncoder(w).Encode(ProjectAPIKeySecret{APIKey: ProjectAPIKey{ID: "key-1", ProjectID: "project-1"}, Secret: "ts_rotated"})
		case "/api/v1/projects/project-1/auth/api-keys/key-1":
			_ = json.NewEncoder(w).Encode(ProjectAPIKeyRevocation{KeyID: "key-1", Revoked: true})
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	client, err := NewClient(Config{APIBaseURL: server.URL, AccessToken: "dashboard-session"})
	if err != nil {
		t.Fatal(err)
	}
	opts := []RequestOption{WithIdempotencyKey("qualification-key")}
	created, err := client.ProjectAPIKeys.Create(t.Context(), "project-1", CreateProjectAPIKeyRequest{Name: "qualification", Scopes: []string{"database:read"}}, opts...)
	if err != nil || created.Secret != "ts_secret" {
		t.Fatalf("create = %+v, error = %v", created, err)
	}
	page, err := client.ProjectAPIKeys.List(t.Context(), "project-1", "", 100)
	if err != nil || !reflect.DeepEqual(page.APIKeys, []ProjectAPIKey{{ID: "key-1"}}) || page.NextCursor != "next" {
		t.Fatalf("list = %+v, error = %v", page, err)
	}
	rotated, err := client.ProjectAPIKeys.Rotate(t.Context(), "project-1", "key-1", opts...)
	if err != nil || rotated.Secret != "ts_rotated" {
		t.Fatalf("rotate = %+v, error = %v", rotated, err)
	}
	revoked, err := client.ProjectAPIKeys.Revoke(t.Context(), "project-1", "key-1", opts...)
	if err != nil || !revoked.Revoked {
		t.Fatalf("revoke = %+v, error = %v", revoked, err)
	}
	want := []string{
		"POST /api/v1/projects/project-1/auth/api-keys",
		"GET /api/v1/projects/project-1/auth/api-keys?limit=100",
		"POST /api/v1/projects/project-1/auth/api-keys/key-1/rotate",
		"DELETE /api/v1/projects/project-1/auth/api-keys/key-1",
	}
	if !reflect.DeepEqual(requests, want) {
		t.Fatalf("requests = %#v, want %#v", requests, want)
	}
}

func TestProjectAPIKeysRejectInvalidInputs(t *testing.T) {
	client := &ProjectAPIKeysClient{c: &Client{}}
	if _, err := client.Create(t.Context(), "", CreateProjectAPIKeyRequest{}); err == nil {
		t.Fatal("blank project accepted")
	}
	if _, err := client.Create(t.Context(), "project", CreateProjectAPIKeyRequest{Name: "key"}); err == nil {
		t.Fatal("empty scopes accepted")
	}
}
