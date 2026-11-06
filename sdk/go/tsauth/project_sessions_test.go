package tsauth

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"
)

const (
	sessionsProject    = "01K6SY8PVAFJ6SW3RT5CY4TQKF"
	sessionsUser       = "01JB3NDEKTSV4RRFFQ69G5FA6D"
	sessionsSession    = "01JB45GM2PXW3RT5CY4TQK8ZF2"
	sessionsSessionTwo = "01JB45GM2PXW3RT5CY4TQK8ZF3"
)

func sessionsBody(items string, next string, present bool) string {
	cursor := "null"
	if present {
		cursor = `"` + next + `"`
	}
	return `{"items":[` + items + `],"next":` + cursor + `}`
}

var sessionsItem = `{"id":"` + sessionsSession + `","user_id":"` + sessionsUser +
	`","email":"a@example.com","created_at":"2026-11-05T10:00:00Z","expires_at":"2026-11-12T10:00:00Z"}`

// One happy-path contract: GET /v1/{project}/sessions with limit and user
// query parameters answers a required-member page, then DELETE
// /v1/{project}/sessions/{session} answers an empty 204, each sent once.
func TestProjectSessionsListAndRevokeHappyPath(t *testing.T) {
	t.Parallel()
	var mu sync.Mutex
	revoked := false
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer fixture" || r.Header.Get("Accept") != "application/json" {
			t.Errorf("headers: %v", r.Header)
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		mu.Lock()
		defer mu.Unlock()
		switch {
		case r.Method == http.MethodGet && r.URL.Path == "/v1/"+sessionsProject+"/sessions":
			if r.URL.Query().Get("limit") != "2" || r.URL.Query().Get("user") != sessionsUser {
				t.Errorf("list query: %v", r.URL.Query())
			}
			second := strings.Replace(sessionsItem, sessionsSession, sessionsSessionTwo, 1)
			w.Header().Set("Content-Type", "application/json")
			io.WriteString(w, sessionsBody(sessionsItem+","+second, "CURSOR", true))
		case r.Method == http.MethodDelete && r.URL.Path == "/v1/"+sessionsProject+"/sessions/"+sessionsSession:
			revoked = true
			w.WriteHeader(http.StatusNoContent)
		default:
			t.Errorf("unexpected request %s %s", r.Method, r.URL.Path)
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	t.Cleanup(srv.Close)
	c, err := New(Config{Issuer: srv.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	u := c.WithToken("fixture")
	ctx := t.Context()
	page, err := u.ProjectSessions(ctx, sessionsProject, ProjectSessionsOptions{Limit: 2, User: sessionsUser})
	if err != nil {
		t.Fatal(err)
	}
	if len(page.Items) != 2 || page.Items[0].ID != sessionsSession || page.Items[1].ID != sessionsSessionTwo ||
		page.Items[0].UserID != sessionsUser || page.Items[0].Email != "a@example.com" ||
		!page.Items[0].CreatedAt.Equal(time.Date(2026, 11, 5, 10, 0, 0, 0, time.UTC)) ||
		!page.Items[0].ExpiresAt.Equal(time.Date(2026, 11, 12, 10, 0, 0, 0, time.UTC)) ||
		page.Next == nil || *page.Next != "CURSOR" {
		t.Fatalf("page: %+v next=%v", page.Items, page.Next)
	}
	if err := u.RevokeProjectSession(ctx, sessionsProject, sessionsSession); err != nil {
		t.Fatal(err)
	}
	if !revoked {
		t.Fatal("revoke was not sent")
	}
}

// Critical contract denials: tenant denial stays a typed sanitized APIError,
// malformed success bodies are refused without values retained, and caller
// cancellation is honored.
func TestProjectSessionsCriticalDenials(t *testing.T) {
	t.Parallel()
	for _, body := range []string{`{"items":null,"next":null}`,
		`{"items":[` + strings.Replace(sessionsItem, `"expires_at":"2026-11-12T10:00:00Z"`, `"expires_at":""`, 1) + `],"next":null}`} {
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", "application/json")
			io.WriteString(w, body)
		}))
		c, cErr := New(Config{Issuer: srv.URL + "/terrascale"})
		if cErr != nil {
			t.Fatal(cErr)
		}
		if _, err := c.WithToken("fixture").ProjectSessions(t.Context(), sessionsProject, ProjectSessionsOptions{}); err == nil {
			t.Fatalf("malformed page %q accepted", body)
		}
		srv.Close()
	}
	for _, tc := range []struct {
		name string
		run  func(*testing.T, *User)
	}{
		{name: "viewer cannot revoke", run: func(t *testing.T, u *User) {
			if err := u.RevokeProjectSession(t.Context(), sessionsProject, sessionsSession); !errors.Is(err, ErrForbiddenRole) {
				t.Fatalf("revoke: %v", err)
			}
		}},
		{name: "malformed session id", run: func(t *testing.T, u *User) {
			if err := u.RevokeProjectSession(t.Context(), sessionsProject, "not-a-ulid"); err == nil {
				t.Fatal("invalid session id accepted")
			}
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(http.StatusForbidden)
				io.WriteString(w, `{"error":{"code":"forbidden_role","message":"insufficient project role"}}`)
			}))
			c, err := New(Config{Issuer: srv.URL + "/terrascale"})
			if err != nil {
				t.Fatal(err)
			}
			tc.run(t, c.WithToken("fixture"))
		})
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	c, err := New(Config{Issuer: "https://auth.example/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := c.WithToken("fixture").ProjectSessions(ctx, sessionsProject, ProjectSessionsOptions{}); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancellation: %v", err)
	}
}
