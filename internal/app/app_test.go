package app

import (
	"net/http"
	"net/http/httptest"
	"testing"

	"terrascale.tech/dashboard/internal/platform/config"
)

func TestSecurityHeaders(t *testing.T) {
	for _, secure := range []bool{false, true} {
		application, err := New(config.Config{Demo: true, SecureCookies: secure})
		if err != nil {
			t.Fatal(err)
		}
		response := httptest.NewRecorder()
		application.Handler.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/health/live", nil))
		header := response.Header()
		if header.Get("X-Content-Type-Options") != "nosniff" {
			t.Errorf("secure=%t: X-Content-Type-Options = %q, want nosniff", secure, header.Get("X-Content-Type-Options"))
		}
		if hsts := header.Get("Strict-Transport-Security"); (hsts != "") != secure {
			t.Errorf("secure=%t: Strict-Transport-Security = %q", secure, hsts)
		}
	}
}
