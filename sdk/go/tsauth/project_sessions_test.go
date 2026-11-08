package tsauth

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
)

// The project-tenant sessions contract of the auth producer, replayed from
// the shared fixture (contracts/auth/project-sessions.json, values verbatim
// from the .NET SDK's fixtures/project-tenant.json): one list including the
// user filter with the producer's pagination, then one revoke against the
// same fixture's method, path and status.
func TestProjectSessionsMatchFixture(t *testing.T) {
	t.Parallel()
	data, err := os.ReadFile("../contracts/auth/project-sessions.json")
	if err != nil {
		t.Skipf("contract fixture unavailable: %v", err)
	}
	var fixture struct {
		ListSessions struct {
			Method   string          `json:"method"`
			Path     string          `json:"path"`
			Status   int             `json:"status"`
			Response json.RawMessage `json:"response"`
		} `json:"list_sessions"`
		RevokeSession struct {
			Method string `json:"method"`
			Path   string `json:"path"`
			Status int    `json:"status"`
		} `json:"revoke_session"`
	}
	if err := json.Unmarshal(data, &fixture); err != nil {
		t.Fatal(err)
	}
	const project, user, session = "01ARZ3NDEKTSV4RRFFQ69G5FA6", "01ARZ3NDEKTSV4RRFFQ69G5FA7", "01ARZ3NDEKTSV4RRFFQ69G5FA8"
	var requests []string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests = append(requests, r.Method+" "+r.URL.Path+"?"+r.URL.RawQuery)
		if s := requests[len(requests)-1]; strings.HasSuffix(s, "?") {
			requests[len(requests)-1] = strings.TrimSuffix(s, "?")
		}
		if r.Header.Get("Authorization") != "Bearer fixture-sign-in" {
			t.Errorf("authorization = %q", r.Header.Get("Authorization"))
		}
		if r.Method == http.MethodGet {
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(fixture.ListSessions.Status)
			_, _ = w.Write([]byte(strings.ReplaceAll(strings.ReplaceAll(string(fixture.ListSessions.Response), "{session}", session), "{user}", user)))
			return
		}
		w.WriteHeader(fixture.RevokeSession.Status)
	}))
	defer server.Close()

	client, err := New(Config{Issuer: server.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	u := client.WithToken("fixture-sign-in")
	page, err := u.ProjectSessions(t.Context(), project, ProjectSessionListOptions{User: user, Limit: 2, After: "opaque+/cursor="})
	if err != nil {
		t.Fatal(err)
	}
	if len(page.Items) != 1 || page.Items[0].ID != session || page.Items[0].UserID != user || page.Items[0].Email == "" || page.Items[0].ExpiresAt.IsZero() {
		t.Fatalf("session page = %+v", page)
	}
	if page.Next != nil {
		t.Fatalf("next = %v, want nil", *page.Next)
	}
	if err := u.RevokeProjectSession(t.Context(), project, session); err != nil {
		t.Fatal(err)
	}
	want := []string{
		fmt.Sprintf("GET /v1/%s/sessions?after=opaque%%2B%%2Fcursor%%3D&limit=2&user=%s", project, user),
		fmt.Sprintf("DELETE /v1/%s/sessions/%s", project, session),
	}
	if len(requests) != 2 {
		t.Fatalf("requests = %v", requests)
	}
	for i := range want {
		if requests[i] != want[i] {
			t.Errorf("request %d = %q, want %q", i, requests[i], want[i])
		}
	}
}
