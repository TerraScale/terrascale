package session_test

import (
	"encoding/base64"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/session"
)

func TestSessionAndCSRFRealHTTPFlow(t *testing.T) {
	store := core.NewStore()
	a, err := store.CreateAccount(core.User{Name: "A", Email: "a@example.com", PasswordHash: "hashed"})
	if err != nil {
		t.Fatal(err)
	}
	b, err := store.CreateAccount(core.User{Name: "B", Email: "b@example.com", PasswordHash: "hashed"})
	if err != nil {
		t.Fatal(err)
	}
	sessions := session.New(store, session.Options{})
	guard := csrf.New(sessions, csrf.Options{})
	get := httptest.NewRequest(http.MethodGet, "http://dashboard.test/login", nil)
	get.Host = "dashboard.test"
	getResult := httptest.NewRecorder()
	token := guard.Token(getResult, get)
	csrfCookie := getResult.Result().Cookies()[0]
	login := httptest.NewRequest(http.MethodPost, "http://dashboard.test/login", nil)
	login.AddCookie(csrfCookie)
	loginResult := httptest.NewRecorder()
	if err := sessions.Start(loginResult, login, a.Principal()); err != nil {
		t.Fatal(err)
	}
	sessionCookie := loginResult.Result().Cookies()[0]
	if sessionCookie.Value == "" || !sessionCookie.HttpOnly || sessionCookie.SameSite != http.SameSiteLaxMode {
		t.Fatal("session cookie lacks opaque secure defaults")
	}
	mutation := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		p, ok := sessions.Principal(r)
		if !ok || p.AccountID != a.ID || p.AccountID == b.ID {
			t.Error("wrong account principal")
		}
		w.WriteHeader(http.StatusNoContent)
	})
	protected := guard.Protect(mutation)
	request := func(origin, submitted string) *httptest.ResponseRecorder {
		values := url.Values{"_csrf": {submitted}}
		r := httptest.NewRequest(http.MethodPost, "http://dashboard.test/mutate", strings.NewReader(values.Encode()))
		r.Host = "dashboard.test"
		r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		r.Header.Set("Origin", origin)
		r.AddCookie(csrfCookie)
		r.AddCookie(sessionCookie)
		w := httptest.NewRecorder()
		protected.ServeHTTP(w, r)
		return w
	}
	if status := request("http://dashboard.test", token).Code; status != http.StatusNoContent {
		t.Fatalf("valid form status %d", status)
	}
	if status := request("http://evil.test", token).Code; status != http.StatusForbidden {
		t.Fatalf("cross-origin status %d", status)
	}
	if status := request("http://dashboard.test", "").Code; status != http.StatusForbidden {
		t.Fatalf("missing token status %d", status)
	}
	forged := base64.RawURLEncoding.EncodeToString(make([]byte, 64))
	forgedForm := url.Values{"_csrf": {forged}}
	forgedRequest := httptest.NewRequest(http.MethodPost, "http://dashboard.test/mutate", strings.NewReader(forgedForm.Encode()))
	forgedRequest.Host = "dashboard.test"
	forgedRequest.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	forgedRequest.AddCookie(&http.Cookie{Name: csrfCookie.Name, Value: forged})
	forgedRequest.AddCookie(sessionCookie)
	forgedResult := httptest.NewRecorder()
	protected.ServeHTTP(forgedResult, forgedRequest)
	if forgedResult.Code != http.StatusForbidden {
		t.Fatalf("forged matching cookie/form status %d", forgedResult.Code)
	}
	bearer := httptest.NewRequest(http.MethodPost, "http://dashboard.test/mutate", nil)
	bearer.Header.Set("Authorization", "Bearer "+sessionCookie.Value)
	if p, ok := sessions.BearerPrincipal(bearer); !ok || p.AccountID != a.ID {
		t.Fatal("issued session bearer failed")
	}
	if p, ok := sessions.BearerPrincipal(httptest.NewRequest(http.MethodPost, "http://dashboard.test/mutate", nil)); ok || p.AccountID != "" {
		t.Fatal("missing bearer accepted")
	}
	sessions.RevokeUser(a.ID, a.User.ID)
	if _, ok := sessions.Principal(func() *http.Request {
		r := httptest.NewRequest(http.MethodGet, "http://dashboard.test/", nil)
		r.AddCookie(sessionCookie)
		return r
	}()); ok {
		t.Fatal("revoked session remained valid")
	}
	secondResponse := httptest.NewRecorder()
	if err := sessions.Start(secondResponse, login, a.Principal()); err != nil {
		t.Fatal(err)
	}
	secondToken := secondResponse.Result().Cookies()[0].Value
	bearerLogout := httptest.NewRequest(http.MethodPost, "http://dashboard.test/api/v1/auth/logout", nil)
	bearerLogout.Header.Set("Authorization", "Bearer "+secondToken)
	sessions.End(httptest.NewRecorder(), bearerLogout)
	if _, ok := sessions.BearerPrincipal(bearerLogout); ok {
		t.Fatal("bearer-only logout left token valid")
	}
}
