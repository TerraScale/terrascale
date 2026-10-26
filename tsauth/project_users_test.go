package tsauth_test

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"

	"terrascale.tech/sdk/tsauth"
)

const directoryID = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
const directoryUserJSON = `{"id":"` + directoryID + `","email":"own@example.test","created_at":"2026-10-26T00:00:00Z","disabled":false}`

type directoryRT func(*http.Request) (*http.Response, error)

func (f directoryRT) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
func directoryUser(t *testing.T, rt directoryRT) *tsauth.User {
	t.Helper()
	c, err := tsauth.New(tsauth.Config{Issuer: "https://auth.example.test/proxy/terrascale", HTTPClient: &http.Client{Transport: rt}})
	if err != nil {
		t.Fatal(err)
	}
	return c.WithToken("private-test-token")
}
func directoryResponse(status int, body string) *http.Response {
	return &http.Response{StatusCode: status, Header: http.Header{"Content-Type": []string{"application/json"}}, Body: io.NopCloser(strings.NewReader(body))}
}

func TestProjectDirectoryRead(t *testing.T) {
	calls := 0
	u := directoryUser(t, func(r *http.Request) (*http.Response, error) {
		calls++
		if r.Method != "GET" || r.Body != nil || r.Header.Get("Authorization") != "Bearer private-test-token" || r.Header.Get("Accept") != "application/json" {
			t.Error("incorrect read/auth")
		}
		deadline, ok := r.Context().Deadline()
		if calls <= 2 && (!ok || time.Until(deadline) > 30*time.Second) {
			t.Error("missing fixed deadline")
		}
		switch calls {
		case 1:
			if r.URL.Path != "/proxy/v1/"+directoryID+"/users" || r.URL.Query().Get("q") != "A +@" || r.URL.Query().Get("limit") != "100" || r.URL.Query().Get("after") != "opaque/+=" {
				t.Error("incorrect list route/query")
			}
			return directoryResponse(200, `{"items":[`+directoryUserJSON+`],"next":"opaque/+="}`), nil
		case 2:
			if r.URL.Path != "/proxy/v1/"+directoryID+"/users/"+directoryID || r.URL.RawQuery != "" {
				t.Error("incorrect get route")
			}
			return directoryResponse(200, directoryUserJSON), nil
		default:
			if r.URL.Path != "/proxy/v1/terrascale/organizations" {
				t.Error("original client tenant mutated")
			}
			return directoryResponse(200, `{"items":[],"next":null}`), nil
		}
	})
	page, err := u.ProjectUsers(t.Context(), directoryID, tsauth.ProjectUserListOptions{Query: "A +@", Limit: 100, After: "opaque/+="})
	if err != nil || page.Next == nil || *page.Next != "opaque/+=" || len(page.Items) != 1 || page.Items[0].Disabled {
		t.Fatalf("page: %v", err)
	}
	user, err := u.ProjectUser(t.Context(), directoryID, directoryID)
	if err != nil || user.CreatedAt.IsZero() {
		t.Fatalf("user: %v", err)
	}
	if _, err := u.Organizations(t.Context(), tsauth.PageOptions{}); err != nil {
		t.Fatal(err)
	}
}

func TestProjectDirectoryRequiredMembers(t *testing.T) {
	for _, bad := range []string{`null`, `{}`, strings.Replace(directoryUserJSON, `,"disabled":false`, "", 1), strings.Replace(directoryUserJSON, `false`, `null`, 1), strings.Replace(directoryUserJSON, directoryID, "bad", 1), strings.Replace(directoryUserJSON, `2026-10-26T00:00:00Z`, `secret-invalid-date`, 1)} {
		var user tsauth.ProjectUser
		if err := json.Unmarshal([]byte(bad), &user); err == nil || strings.Contains(err.Error(), "secret") {
			t.Error("accepted invalid user or exposed response")
		}
	}
	for _, bad := range []string{`{}`, `{"items":null,"next":null}`, `{"items":[]}`, `{"items":[null],"next":null}`} {
		var page tsauth.ProjectUserPage
		if json.Unmarshal([]byte(bad), &page) == nil {
			t.Error("accepted invalid page")
		}
	}
	var page tsauth.ProjectUserPage
	if json.Unmarshal([]byte(`{"items":[],"next":null}`), &page) != nil || page.Next != nil {
		t.Error("lost nullable cursor")
	}
}

func TestProjectDirectoryValidationAndCancellation(t *testing.T) {
	calls := 0
	u := directoryUser(t, func(r *http.Request) (*http.Response, error) { calls++; return nil, errors.New("private-test-token") })
	for _, id := range []string{"", "../secret", strings.ToLower(directoryID)} {
		if _, err := u.ProjectUser(t.Context(), id, directoryID); err == nil {
			t.Error("accepted project ID")
		}
		if _, err := u.ProjectUser(t.Context(), directoryID, id); err == nil {
			t.Error("accepted user ID")
		}
	}
	for _, limit := range []int{-1, 101} {
		if _, err := u.ProjectUsers(t.Context(), directoryID, tsauth.ProjectUserListOptions{Limit: limit}); err == nil {
			t.Error("accepted limit")
		}
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := u.ProjectUser(ctx, directoryID, directoryID); !errors.Is(err, context.Canceled) {
		t.Error("lost cancellation")
	}
	if calls != 0 {
		t.Fatal("invalid request dispatched")
	}
	if _, err := u.ProjectUser(t.Context(), directoryID, directoryID); err == nil || strings.Contains(err.Error(), "private-test-token") {
		t.Error("transport diagnostics leaked")
	}
}

func TestProjectDirectoryReadRefusals(t *testing.T) {
	for _, status := range []int{400, 401, 403, 404, 429, 503, 307} {
		calls := 0
		u := directoryUser(t, func(r *http.Request) (*http.Response, error) {
			calls++
			res := directoryResponse(status, `{"error":{"code":"not_found","message":"private-test-token"}}`)
			res.Header.Set("Retry-After", "7")
			return res, nil
		})
		_, err := u.ProjectUser(t.Context(), directoryID, directoryID)
		var api *tsauth.APIError
		if !errors.As(err, &api) || api.Status != status || !errors.Is(err, tsauth.ErrNotFound) || api.RetryAfter != 7*time.Second || calls != 1 || strings.Contains(err.Error(), "private-test-token") {
			t.Errorf("status %d: %v", status, err)
		}
	}
	for _, body := range []string{`null`, directoryUserJSON + strings.Repeat(" ", 8<<20)} {
		u := directoryUser(t, func(r *http.Request) (*http.Response, error) { return directoryResponse(200, body), nil })
		if _, err := u.ProjectUser(t.Context(), directoryID, directoryID); err == nil {
			t.Error("accepted invalid/oversized result")
		}
	}
}
