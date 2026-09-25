package organization_test

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/organization"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/session"
)

func TestLegacyOrganizationSwitchUsesExistingSessionAndCSRFPolicy(t *testing.T) {
	store := core.NewStore()
	owner, err := store.CreateAccount(core.User{Name: "Owner", Email: "owner@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	personalID := owner.ActiveOrganizationID
	service := organization.NewService(store)
	business, err := service.CreateBusiness(owner.Principal(), "Business")
	if err != nil {
		t.Fatal(err)
	}
	other, err := store.CreateAccount(core.User{Name: "Other", Email: "other@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}

	sessions := session.New(store, session.Options{})
	guard := csrf.New(sessions, csrf.Options{})
	mux := http.NewServeMux()
	organization.Mount(mux, core.Deps{Store: store, Sessions: sessions, CSRF: guard})
	handler := guard.Protect(mux)

	account, ok := store.Snapshot(owner.ID)
	if !ok {
		t.Fatal("owner account disappeared")
	}
	issue := httptest.NewRecorder()
	issueRequest := httptest.NewRequest(http.MethodGet, "http://dashboard.test/organization", nil)
	if err := sessions.Start(issue, issueRequest, account.Principal()); err != nil {
		t.Fatal(err)
	}
	token := guard.Token(issue, issueRequest)
	var sessionCookie, csrfCookie *http.Cookie
	for _, cookie := range issue.Result().Cookies() {
		switch cookie.Name {
		case "ts_dashboard_session":
			sessionCookie = cookie
		case "ts_dashboard_csrf":
			csrfCookie = cookie
		}
	}
	if sessionCookie == nil || csrfCookie == nil {
		t.Fatal("session and CSRF cookies were not issued")
	}

	post := func(path string, values url.Values, validCSRF bool) *httptest.ResponseRecorder {
		t.Helper()
		if validCSRF {
			values.Set("_csrf", token)
		}
		r := httptest.NewRequest(http.MethodPost, "http://dashboard.test"+path, strings.NewReader(values.Encode()))
		r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		r.AddCookie(sessionCookie)
		r.AddCookie(csrfCookie)
		w := httptest.NewRecorder()
		handler.ServeHTTP(w, r)
		for _, cookie := range w.Result().Cookies() {
			if cookie.Name == "ts_dashboard_session" {
				sessionCookie = cookie
			}
		}
		return w
	}
	active := func() string {
		t.Helper()
		updated, ok := store.Snapshot(owner.ID)
		if !ok {
			t.Fatal("owner account disappeared")
		}
		return updated.ActiveOrganizationID
	}

	legacy := post("/api/v1/dashboard/organization/switch", url.Values{"organizationId": {personalID}}, true)
	if legacy.Code != http.StatusSeeOther || legacy.Header().Get("Location") != "/" || active() != personalID {
		t.Fatalf("legacy switch = %d at %q, active %q", legacy.Code, legacy.Header().Get("Location"), active())
	}
	current := post("/api/v1/dashboard/switch-organization", url.Values{"organizationId": {business.ID}}, true)
	if current.Code != http.StatusSeeOther || current.Header().Get("Location") != "/organization" || active() != business.ID {
		t.Fatalf("current switch = %d at %q, active %q", current.Code, current.Header().Get("Location"), active())
	}
	unsafeReturn := post("/api/v1/dashboard/organization/switch", url.Values{"organizationId": {personalID}, "returnUrl": {"//evil.example"}}, true)
	if unsafeReturn.Code != http.StatusSeeOther || unsafeReturn.Header().Get("Location") != "/" || active() != personalID {
		t.Fatalf("unsafe legacy return = %d at %q, active %q", unsafeReturn.Code, unsafeReturn.Header().Get("Location"), active())
	}
	safeReturn := post("/api/v1/dashboard/switch-organization", url.Values{"organizationId": {business.ID}, "returnUrl": {"/databases?tab=details"}}, true)
	if safeReturn.Code != http.StatusSeeOther || safeReturn.Header().Get("Location") != "/databases?tab=details" || active() != business.ID {
		t.Fatalf("safe return = %d at %q, active %q", safeReturn.Code, safeReturn.Header().Get("Location"), active())
	}
	invalidCSRF := post("/api/v1/dashboard/organization/switch", url.Values{"organizationId": {personalID}}, false)
	if invalidCSRF.Code != http.StatusForbidden || active() != business.ID {
		t.Fatalf("invalid CSRF = %d, active %q", invalidCSRF.Code, active())
	}
	foreign := post("/api/v1/dashboard/organization/switch", url.Values{"organizationId": {other.ActiveOrganizationID}}, true)
	if (foreign.Code != http.StatusForbidden && foreign.Code != http.StatusNotFound) || active() != business.ID {
		t.Fatalf("foreign switch = %d, active %q", foreign.Code, active())
	}
}
