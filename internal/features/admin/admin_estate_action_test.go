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
	mux.ServeHTTP(page, httptest.NewRequest(http.MethodGet, "/admin/estate?tab=operations", nil))
	if page.Code != http.StatusOK {
		t.Fatalf("GET /admin/estate status = %d, want 200", page.Code)
	}
	body := page.Body.String()
	for _, expected := range []string{
		"Product actions", "Recorded actions", "Record only: this demo does not change infrastructure, billing, tenant state, or access.",
		`action="/api/v1/admin/estate/action"`,
	} {
		if !strings.Contains(body, expected) {
			t.Errorf("estate page is missing %q", expected)
		}
	}
	if !adminControlHasAttributes(body, "select", "action", "required") {
		t.Error("estate action selector is not required")
	}
	if !adminControlHasAttributes(body, "input", "targetId", "required") {
		t.Error("estate target input is not required")
	}
	if !adminControlHasAttributes(body, "textarea", "reason", "rows=3", "minlength=3", "maxlength=1000", "required") {
		t.Error("estate reason field is missing its required 3–1000 character constraints")
	}
	for _, action := range estateActionKinds {
		if !strings.Contains(body, `value="`+action+`"`) {
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
	location, err := url.Parse(response.Header().Get("Location"))
	if err != nil || location.Path != "/admin/estate" || location.Query().Get("tab") != "operations" || location.Query().Get("notice") == "" {
		t.Fatalf("record action redirect = %q, want estate notice; parse error=%v", response.Header().Get("Location"), err)
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

func adminControlHasAttributes(body, element, name string, requiredAttributes ...string) bool {
	firstFragment := true
	for fragment := range strings.SplitSeq(body, "<"+element) {
		if firstFragment {
			firstFragment = false
			continue
		}
		tag, _, found := strings.Cut(fragment, ">")
		if !found {
			continue
		}
		attributes := make(map[string]string)
		for _, field := range strings.Fields(tag) {
			key, value, hasValue := strings.Cut(field, "=")
			if hasValue {
				attributes[key] = strings.Trim(value, `"'`)
			} else {
				attributes[key] = ""
			}
		}
		if attributes["name"] != name {
			continue
		}
		for _, required := range requiredAttributes {
			key, value, hasValue := strings.Cut(required, "=")
			got, found := attributes[key]
			if !found || hasValue && got != value {
				return false
			}
		}
		return true
	}
	return false
}

func TestAdminEstateTabsUseLocalInventoryAndAllowlistedQueries(t *testing.T) {
	store := core.NewStore()
	account := newAdminAccount(t, store, "estate-tabs-admin@example.test", true)
	seedAdminDatabase(t, store, account, "Estate database", "GRU")
	mux := adminTestMux(t, store, account.Principal(), adminTestCSRF{valid: true})

	for _, test := range []struct {
		name        string
		query       string
		wantCurrent string
		wantContent []string
		wantAbsent  []string
	}{
		{
			name:        "default regions tab",
			query:       "/admin/estate",
			wantCurrent: `href="/admin/estate?tab=regions" aria-current="page"`,
			wantContent: []string{"São Paulo", "San Francisco", "Frankfurt", "GRU", "SFO", "FRA", "Provider availability", "Not reported"},
			wantAbsent:  []string{`action="/api/v1/admin/estate/action"`},
		},
		{
			name:        "hosts tab shows only local records",
			query:       "/admin/estate?tab=hosts",
			wantCurrent: `href="/admin/estate?tab=hosts" aria-current="page"`,
			wantContent: []string{"Provider host inventory and host health are unavailable", "not provider hosts or host telemetry", "Estate database", "db-admin-fixture", "Recorded status"},
			wantAbsent:  []string{`action="/api/v1/admin/estate/action"`, "Version"},
		},
		{
			name:        "operations tab retains recorded actions",
			query:       "/admin/estate?tab=operations",
			wantCurrent: `href="/admin/estate?tab=operations" aria-current="page"`,
			wantContent: []string{`action="/api/v1/admin/estate/action"`, "Product actions", "Recorded actions", "Record only:"},
			wantAbsent:  []string{`id="admin-estate-regions-title"`, `id="admin-estate-hosts-title"`},
		},
		{
			name:        "unrecognized tab falls back to regions",
			query:       "/admin/estate?tab=health",
			wantCurrent: `href="/admin/estate?tab=regions" aria-current="page"`,
			wantContent: []string{"Regions", "GRU", "SFO", "FRA"},
			wantAbsent:  []string{`id="admin-estate-hosts-title"`, `id="admin-estate-actions-title"`},
		},
		{
			name:        "host filter keeps its tab selected",
			query:       "/admin/estate?tab=hosts&q=not-a-database",
			wantCurrent: `href="/admin/estate?tab=hosts" aria-current="page"`,
			wantContent: []string{`name="tab" value="hosts"`, "No matching database records"},
			wantAbsent:  []string{"db-admin-fixture"},
		},
	} {
		t.Run(test.name, func(t *testing.T) {
			response := httptest.NewRecorder()
			mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, test.query, nil))
			body := response.Body.String()
			if response.Code != http.StatusOK {
				t.Fatalf("GET %s status = %d, want 200; body=%s", test.query, response.Code, body)
			}
			if !strings.Contains(body, test.wantCurrent) {
				t.Errorf("GET %s did not mark tab current: missing %q", test.query, test.wantCurrent)
			}
			for _, expected := range test.wantContent {
				if !strings.Contains(body, expected) {
					t.Errorf("GET %s is missing %q", test.query, expected)
				}
			}
			for _, unexpected := range test.wantAbsent {
				if strings.Contains(body, unexpected) {
					t.Errorf("GET %s unexpectedly contains %q", test.query, unexpected)
				}
			}
		})
	}

	data, err := NewService(store).page(account.Principal(), "estate", "")
	if err != nil {
		t.Fatalf("load estate local inventory: %v", err)
	}
	if data.EstateRegionCount != 3 || len(data.Regions) != 3 || data.EstateDatabaseCount != 1 {
		t.Fatalf("estate local inventory = region count %d, rows %d, database count %d; want 3/3/1", data.EstateRegionCount, len(data.Regions), data.EstateDatabaseCount)
	}
	regions := make(map[string]estateRow, len(data.Regions))
	for _, region := range data.Regions {
		regions[region.Region] = region
	}
	for _, code := range []string{"GRU", "SFO", "FRA"} {
		if _, ok := regions[code]; !ok {
			t.Errorf("supported demo region %q missing from local inventory", code)
		}
	}
	if regions["GRU"].ObservedDatabases != 1 {
		t.Errorf("GRU recorded database count = %d, want 1", regions["GRU"].ObservedDatabases)
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
