package app

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/platform/config"
)

func TestPagesShowOnlyTheDashboardsOwnMessages(t *testing.T) {
	application, err := New(config.Config{Demo: true})
	if err != nil {
		t.Fatal(err)
	}
	const planted = "Your account is suspended. Call +1 555 0100."
	for _, tc := range []struct {
		name    string
		path    string
		headers map[string]string
		shown   bool
	}{
		{"typed or emailed link", "/login", map[string]string{"Sec-Fetch-Site": "none"}, false},
		{"link on another site", "/login", map[string]string{"Sec-Fetch-Site": "cross-site"}, false},
		{"link on a sibling subdomain", "/login", map[string]string{"Sec-Fetch-Site": "same-site"}, false},
		{"no fetch metadata or referer", "/login", nil, false},
		{"foreign referer", "/login", map[string]string{"Referer": "https://evil.example/login"}, false},
		{"registration page link", "/onboarding", map[string]string{"Sec-Fetch-Site": "none"}, false},
		{"dashboard redirect", "/login", map[string]string{"Sec-Fetch-Site": "same-origin"}, true},
		{"dashboard referer without fetch metadata", "/login", map[string]string{"Referer": "http://example.com/login"}, true},
		{"registration page redirect", "/onboarding", map[string]string{"Sec-Fetch-Site": "same-origin"}, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			request := httptest.NewRequest(http.MethodGet, tc.path+"?returnTo=%2Fdatabases&error="+url.QueryEscape(planted), nil)
			for name, value := range tc.headers {
				request.Header.Set(name, value)
			}
			response := httptest.NewRecorder()
			application.Handler.ServeHTTP(response, request)
			body := response.Body.String()
			if response.Code != http.StatusOK || strings.Contains(body, planted) != tc.shown {
				t.Fatalf("GET %s = %d, message shown %t, want %t", tc.path, response.Code, strings.Contains(body, planted), tc.shown)
			}
			if tc.path == "/login" && !strings.Contains(body, `name="redirectTo" value="/databases"`) {
				t.Fatal("dropping the message also dropped the other query parameters")
			}
		})
	}
}
