package notfound_test

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/app"
	"terrascale.tech/dashboard/internal/platform/config"
)

func TestMountedNotFoundResponses(t *testing.T) {
	application, err := app.New(config.Config{Demo: true})
	if err != nil {
		t.Fatal(err)
	}

	request := func(method, path string, headers http.Header, cookie *http.Cookie) *httptest.ResponseRecorder {
		t.Helper()
		r := httptest.NewRequest(method, "http://dashboard.test"+path, nil)
		if headers != nil {
			r.Header = headers.Clone()
		}
		if cookie != nil {
			r.AddCookie(cookie)
		}
		w := httptest.NewRecorder()
		application.Handler.ServeHTTP(w, r)
		return w
	}

	for _, path := range []string{"/no-such-page", "/apiary/no-such-page"} {
		w := request(http.MethodGet, path, nil, nil)
		if w.Code != http.StatusNotFound || w.Header().Get("Content-Type") != "text/html; charset=utf-8" ||
			!strings.Contains(w.Body.String(), "/img/404-illustration.webp") ||
			!strings.Contains(w.Body.String(), "/img/404-number.webp") ||
			!strings.Contains(w.Body.String(), "/brand/logo-terrascale-principal.svg") ||
			!strings.Contains(w.Body.String(), "Back to dashboard") ||
			!strings.Contains(w.Body.String(), "https://terrascale.tech/contact") {
			t.Errorf("GET %s returned %d with %q: %s", path, w.Code, w.Header().Get("Content-Type"), w.Body.String())
		}
	}

	for _, tc := range []struct {
		name, method, path string
		headers            http.Header
	}{
		{"api", http.MethodGet, "/api/v1/does-not-exist", nil},
		{"api root", http.MethodGet, "/api", nil},
		{"json", http.MethodGet, "/no-such-page", http.Header{"Accept": {"text/html, application/json"}}},
		{"event stream", http.MethodGet, "/no-such-page", http.Header{"Accept": {"text/html, text/event-stream"}}},
		{"datastar", http.MethodGet, "/no-such-page", http.Header{"Accept": {"text/html"}, "Datastar-Request": {""}}},
		{"head", http.MethodHead, "/no-such-page", nil},
	} {
		t.Run(tc.name, func(t *testing.T) {
			w := request(tc.method, tc.path, tc.headers, nil)
			if w.Code != http.StatusNotFound || strings.Contains(w.Body.String(), "404-illustration.webp") ||
				(tc.method == http.MethodHead && w.Body.Len() != 0) {
				t.Fatalf("%s %s returned %d with unexpected body %q", tc.method, tc.path, w.Code, w.Body.String())
			}
		})
	}

	account, ok := application.Store.FindByEmail("mario@terrascale.tech")
	if !ok {
		t.Fatal("demo account unavailable for authenticated route checks")
	}
	start := httptest.NewRecorder()
	if err := application.Deps.Sessions.Start(start, httptest.NewRequest(http.MethodGet, "http://dashboard.test/", nil), account.Principal()); err != nil {
		t.Fatal(err)
	}
	var sessionCookie *http.Cookie
	for _, cookie := range start.Result().Cookies() {
		if cookie.Name == "ts_dashboard_session" {
			sessionCookie = cookie
			break
		}
	}
	if sessionCookie == nil {
		t.Fatal("session cookie was not issued")
	}

	missingDatabase := request(http.MethodGet, "/databases/db_does_not_exist", nil, sessionCookie)
	if missingDatabase.Code != http.StatusNotFound || !strings.Contains(missingDatabase.Body.String(), "404-illustration.webp") {
		t.Fatalf("missing database returned %d: %s", missingDatabase.Code, missingDatabase.Body.String())
	}
	unknownAdminAnonymous := request(http.MethodGet, "/admin/no-such-page", nil, nil)
	if unknownAdminAnonymous.Code != http.StatusSeeOther || unknownAdminAnonymous.Header().Get("Location") != "/login" {
		t.Fatalf("anonymous admin route returned %d at %q", unknownAdminAnonymous.Code, unknownAdminAnonymous.Header().Get("Location"))
	}
	unknownAdminSignedIn := request(http.MethodGet, "/admin/no-such-page", nil, sessionCookie)
	if unknownAdminSignedIn.Code != http.StatusNotFound || !strings.Contains(unknownAdminSignedIn.Body.String(), "404-illustration.webp") {
		t.Fatalf("signed-in admin route returned %d: %s", unknownAdminSignedIn.Code, unknownAdminSignedIn.Body.String())
	}

	for _, path := range []string{"/img/404-number.webp", "/img/404-illustration.webp"} {
		asset := request(http.MethodGet, path, nil, nil)
		if asset.Code != http.StatusOK || asset.Body.Len() == 0 || asset.Header().Get("Content-Type") != "image/webp" {
			t.Fatalf("GET %s returned %d with type %q and %d bytes", path, asset.Code, asset.Header().Get("Content-Type"), asset.Body.Len())
		}
	}
}
