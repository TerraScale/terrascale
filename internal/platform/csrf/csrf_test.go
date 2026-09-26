package csrf

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

type noSessions struct{ core.Sessions }

func (noSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return core.Principal{}, false
}

func TestProtectBoundsTheFormItParses(t *testing.T) {
	guard := New(noSessions{}, Options{})
	token := guard.Token(httptest.NewRecorder(), httptest.NewRequest(http.MethodGet, "/", nil))
	for _, tc := range []struct {
		name   string
		token  string
		size   int
		status int
	}{
		{name: "valid form", token: token, size: 1 << 20, status: http.StatusNoContent},
		{name: "oversized form", token: token, size: maxFormBody, status: http.StatusRequestEntityTooLarge},
		{name: "missing token", size: 16, status: http.StatusForbidden},
	} {
		t.Run(tc.name, func(t *testing.T) {
			reached := false
			handler := guard.Protect(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				reached = true
				w.WriteHeader(http.StatusNoContent)
			}))
			body := url.Values{"_csrf": {tc.token}, "document": {strings.Repeat("x", tc.size)}}.Encode()
			request := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/data/documents", strings.NewReader(body))
			request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
			request.AddCookie(&http.Cookie{Name: "ts_dashboard_csrf", Value: token})
			response := httptest.NewRecorder()
			handler.ServeHTTP(response, request)
			if response.Code != tc.status || reached != (tc.status == http.StatusNoContent) {
				t.Fatalf("POST = %d (handler reached: %t), want %d", response.Code, reached, tc.status)
			}
		})
	}
}
