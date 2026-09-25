package admin

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"reflect"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

func TestAdminEstateActionRecordsAuditOnly(t *testing.T) {
	store := core.NewStore()
	account := newAdminAccount(t, store, "estate-admin@example.test", true)
	seedAdminDatabase(t, store, account, "Estate database", "GRU")
	mux := adminTestMux(t, store, account.Principal(), adminTestCSRF{valid: true})

	page := httptest.NewRecorder()
	mux.ServeHTTP(page, httptest.NewRequest(http.MethodGet, "/admin/estate", nil))
	if page.Code != http.StatusOK {
		t.Fatalf("GET /admin/estate status = %d, want 200", page.Code)
	}
	for _, expected := range []string{
		"Product actions", "Recorded actions", "Record only: this demo does not change infrastructure, billing, tenant state, or access.",
		`action="/api/v1/admin/estate/action"`, `name="targetId" required`, `name="reason" rows="3" minlength="3" maxlength="1000" required`,
	} {
		if !strings.Contains(page.Body.String(), expected) {
			t.Errorf("estate page is missing %q", expected)
		}
	}
	for _, action := range estateActionKinds {
		if !strings.Contains(page.Body.String(), `value="`+action+`"`) {
			t.Errorf("estate action form is missing allowlisted action %q", action)
		}
	}

	beforeAccount, ok := store.Snapshot(account.ID)
	if !ok {
		t.Fatal("admin account is missing before the action")
	}
	beforePlans := store.Plans()
	started := time.Now().UTC()
	response := adminPost(t, mux, "/api/v1/admin/estate/action", url.Values{
		"action": {"pause"}, "targetId": {"db-admin-fixture"}, "reason": {"Incident review requested"},
	})
	ended := time.Now().UTC()
	if response.Code != http.StatusSeeOther {
		t.Fatalf("record action status = %d, body=%s", response.Code, response.Body.String())
	}
	location, err := url.Parse(response.Header.Get("Location"))
	if err != nil || location.Path != "/admin/estate" || location.Query().Get("notice") == "" {
		t.Fatalf("record action redirect = %q, want estate notice; parse error=%v", response.Header.Get("Location"), err)
	}

	actions, err := store.PlatformAdminActions(account.Principal())
	if err != nil {
		t.Fatalf("read recorded actions: %v", err)
	}
	if len(actions) != 1 {
		t.Fatalf("recorded action count = %d, want 1", len(actions))
	}
	got := actions[0]
	if got.ActorUserID != account.User.ID || got.Action != "pause" || got.Target != "db-admin-fixture" || got.Reason != "Incident review requested" {
		t.Fatalf("recorded action = %+v", got)
	}
	if got.RecordedAt.Before(started) || got.RecordedAt.After(ended) {
		t.Fatalf("recorded at %s, want a time between %s and %s", got.RecordedAt, started, ended)
	}

	afterAccount, ok := store.Snapshot(account.ID)
	if !ok || !reflect.DeepEqual(beforeAccount, afterAccount) {
		t.Fatalf("estate action changed account, organization, project, database, or billing state")
	}
	afterPlans := store.Plans()
	if len(beforePlans) != len(afterPlans) {
		t.Fatalf("estate action changed plan count from %d to %d", len(beforePlans), len(afterPlans))
	}
	for _, plan := range beforePlans {
		if gotPlan, exists := store.Plan(plan.ID); !exists || gotPlan != plan {
			t.Fatalf("estate action changed plan %q: got (%+v, %t), want %+v", plan.ID, gotPlan, exists, plan)
		}
	}

	loggedPage := httptest.NewRecorder()
	mux.ServeHTTP(loggedPage, httptest.NewRequest(http.MethodGet, location.RequestURI(), nil))
	for _, expected := range []string{
		"Action completed and recorded in the audit log.", "pause", "db-admin-fixture", "Incident review requested", account.User.ID,
	} {
		if loggedPage.Code != http.StatusOK || !strings.Contains(loggedPage.Body.String(), expected) {
			t.Fatalf("GET %s status/body missing %q: status=%d body=%s", location.RequestURI(), expected, loggedPage.Code, loggedPage.Body.String())
		}
	}
}

func TestAdminEstateActionRejectsMalformedAndUnauthorizedRequests(t *testing.T) {
	store := core.NewStore()
	account := newAdminAccount(t, store, "estate-admin@example.test", true)
	member := newAdminAccount(t, store, "estate-member@example.test", false)
	mux := adminTestMux(t, store, account.Principal(), adminTestCSRF{valid: true})

	tooLongReason := strings.Repeat("r", 1001)
	for _, test := range []struct {
		name   string
		values url.Values
	}{
		{name: "unknown action", values: url.Values{"action": {"delete"}, "targetId": {"db-1"}, "reason": {"valid reason"}}},
		{name: "wrong case action", values: url.Values{"action": {"Pause"}, "targetId": {"db-1"}, "reason": {"valid reason"}}},
		{name: "empty target", values: url.Values{"action": {"pause"}, "targetId": {""}, "reason": {"valid reason"}}},
		{name: "whitespace target", values: url.Values{"action": {"pause"}, "targetId": {"  "}, "reason": {"valid reason"}}},
		{name: "short reason", values: url.Values{"action": {"pause"}, "targetId": {"db-1"}, "reason": {"ab"}}},
		{name: "empty reason", values: url.Values{"action": {"pause"}, "targetId": {"db-1"}, "reason": {"  "}}},
		{name: "long reason", values: url.Values{"action": {"pause"}, "targetId": {"db-1"}, "reason": {tooLongReason}}},
	} {
		t.Run(test.name, func(t *testing.T) {
			response := adminPost(t, mux, "/api/v1/admin/estate/action", test.values)
			if response.Code != http.StatusSeeOther || !strings.Contains(response.Header().Get("Location"), "error=") {
				t.Fatalf("malformed action response = (%d, %q), want error redirect", response.Code, response.Header().Get("Location"))
			}
		})
	}

	invalidCSRF := adminTestMux(t, store, account.Principal(), adminTestCSRF{valid: false})
	response := adminPost(t, invalidCSRF, "/api/v1/admin/estate/action", url.Values{
		"action": {"pause"}, "targetId": {"db-1"}, "reason": {"valid reason"},
	})
	if response.Code != http.StatusForbidden {
		t.Fatalf("invalid CSRF response = %d, want 403", response.Code)
	}

	forged := member.Principal()
	forged.PlatformAdmin = true
	forgedMux := adminTestMux(t, store, forged, adminTestCSRF{valid: true})
	response = adminPost(t, forgedMux, "/api/v1/admin/estate/action", url.Values{
		"action": {"pause"}, "targetId": {"db-1"}, "reason": {"valid reason"},
	})
	if response.Code != http.StatusForbidden {
		t.Fatalf("forged administrator response = %d, want 403", response.Code)
	}

	actions, err := store.PlatformAdminActions(account.Principal())
	if err != nil {
		t.Fatalf("read action log after rejected requests: %v", err)
	}
	if len(actions) != 0 {
		t.Fatalf("rejected requests appended actions: %+v", actions)
	}
}
