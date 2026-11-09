package tsauth

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"
)

func TestExportProjectUsersMatchFixture(t *testing.T) {
	t.Parallel()
	data, err := os.ReadFile("../contracts/auth/project-user-export.json")
	if err != nil {
		t.Skipf("contract fixture unavailable: %v", err)
	}
	var fixture struct {
		ExportUsers struct {
			Method   string          `json:"method"`
			Path     string          `json:"path"`
			Status   int             `json:"status"`
			Response json.RawMessage `json:"response"`
		} `json:"export_users"`
	}
	if err := json.Unmarshal(data, &fixture); err != nil {
		t.Fatal(err)
	}
	const project, user = "01ARZ3NDEKTSV4RRFFQ69G5FA6", "01ARZ3NDEKTSV4RRFFQ69G5FA7"
	var requests []string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests = append(requests, r.Method+" "+r.URL.Path+"?"+r.URL.RawQuery)
		if s := requests[len(requests)-1]; strings.HasSuffix(s, "?") {
			requests[len(requests)-1] = strings.TrimSuffix(s, "?")
		}
		if r.Header.Get("Authorization") != "Bearer fixture-sign-in" {
			t.Errorf("authorization = %q", r.Header.Get("Authorization"))
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(fixture.ExportUsers.Status)
		_, _ = w.Write([]byte(strings.ReplaceAll(string(fixture.ExportUsers.Response), "{user}", user)))
	}))
	defer server.Close()

	client, err := New(Config{Issuer: server.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	u := client.WithToken("fixture-sign-in")
	page, err := u.ExportProjectUsers(t.Context(), project, 2, "opaque+/cursor=")
	if err != nil {
		t.Fatal(err)
	}
	if len(page.Items) != 1 {
		t.Fatalf("items = %+v", page.Items)
	}
	got := page.Items[0]
	if got.ID != user || got.Email != "synthetic@example.test" || !got.EmailVerified || !got.Disabled ||
		!got.CreatedAt.Equal(time.Date(2026, 10, 13, 0, 0, 0, 0, time.UTC)) {
		t.Fatalf("directory user = %+v", got)
	}
	if len(got.LinkedProviders) != 1 || got.LinkedProviders[0] != (ProjectLinkedProvider{Provider: "github", ClientID: "synthetic-public-client", Subject: "synthetic-subject"}) {
		t.Fatalf("linked providers = %+v", got.LinkedProviders)
	}
	if page.Next == nil || *page.Next != "next+/cursor=" {
		t.Fatalf("next = %v", page.Next)
	}
	if !page.CreatedBefore.Equal(time.Date(2026, 10, 25, 0, 0, 0, 0, time.UTC)) {
		t.Fatalf("created_before = %v", page.CreatedBefore)
	}
	want := []string{fmt.Sprintf("GET /v1/%s/users/export?after=opaque%%2B%%2Fcursor%%3D&limit=2", project)}
	if len(requests) != 1 {
		t.Fatalf("requests = %v", requests)
	}
	if requests[0] != want[0] {
		t.Errorf("request = %q, want %q", requests[0], want[0])
	}
}
