package app

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"testing"
)

func TestDropForeignMessagesKeepsOnlyTheDashboardsOwn(t *testing.T) {
	const planted = "Your account is suspended. Call +1 555 0100."
	var seen url.Values
	handler := dropForeignMessages(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		seen = r.URL.Query()
	}))
	for _, tc := range []struct {
		name    string
		method  string
		headers map[string]string
		kept    bool
	}{
		{"typed or emailed link", http.MethodGet, map[string]string{"Sec-Fetch-Site": "none"}, false},
		{"link on another site", http.MethodGet, map[string]string{"Sec-Fetch-Site": "cross-site"}, false},
		{"link on a sibling subdomain", http.MethodGet, map[string]string{"Sec-Fetch-Site": "same-site"}, false},
		{"no fetch metadata or referer", http.MethodGet, nil, false},
		{"foreign referer", http.MethodGet, map[string]string{"Referer": "https://evil.example/settings"}, false},
		{"foreign HEAD", http.MethodHead, map[string]string{"Sec-Fetch-Site": "cross-site"}, false},
		{"dashboard redirect", http.MethodGet, map[string]string{"Sec-Fetch-Site": "same-origin"}, true},
		{"dashboard referer without fetch metadata", http.MethodGet, map[string]string{"Referer": "http://example.com/settings"}, true},
		{"form post", http.MethodPost, map[string]string{"Sec-Fetch-Site": "cross-site"}, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			query := url.Values{"tab": {"identity"}, "error": {planted}, "notice": {planted}, "toast": {planted}, "invitation": {planted}, "draftScopes": {planted}}
			request := httptest.NewRequest(tc.method, "/settings?"+query.Encode(), nil)
			for name, value := range tc.headers {
				request.Header.Set(name, value)
			}
			seen = nil
			handler.ServeHTTP(httptest.NewRecorder(), request)
			for _, name := range []string{"error", "notice", "toast", "invitation", "draftScopes"} {
				if got := seen.Get(name); (got == planted) != tc.kept {
					t.Errorf("%s = %q, kept %t, want %t", name, got, got == planted, tc.kept)
				}
			}
			if seen.Get("tab") != "identity" {
				t.Errorf("tab = %q; dropping the messages dropped other parameters", seen.Get("tab"))
			}
		})
	}
}
