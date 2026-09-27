package querysubscriptions

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/session"
)

// A browser whose session ended posts a valid form: it must be sent to sign in
// again, as every other form does, not shown a bare 401.
func TestSignedOutFormPostReturnsToLogin(t *testing.T) {
	store := core.NewStore()
	sessions := session.New(store, session.Options{})
	guard := csrf.New(sessions, csrf.Options{})
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: sessions, CSRF: guard, Tools: core.NewTools(store), Demo: true})

	issued := httptest.NewRecorder()
	token := guard.Token(issued, httptest.NewRequest(http.MethodGet, "/", nil))
	form := url.Values{"configId": {"cfg_1"}, "_csrf": {token}}
	request := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/subscriptions/pause", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.Header.Set("Accept", "text/html")
	for _, cookie := range issued.Result().Cookies() {
		request.AddCookie(cookie)
	}
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if location := response.Header().Get("Location"); response.Code != http.StatusSeeOther || location != "/login" {
		t.Fatalf("signed-out pause = %d to %q, want a redirect to /login", response.Code, location)
	}
}
