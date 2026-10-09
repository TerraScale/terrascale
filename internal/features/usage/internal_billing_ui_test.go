package usage

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"reflect"
	"strings"
	"testing"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/csrf"
)

func TestInternalSpendingFormAuditsOnlySubmittedChanges(t *testing.T) {
	service, principal, _ := internalUsageFixture(t, memstore.New())
	deps := service.deps
	deps.Sessions = reviewSessions{principal: principal}
	deps.CSRF = csrf.New(deps.Sessions, csrf.Options{})
	deps.Render = viewRenderer{}
	deps.Tools = core.NewTools(deps.Store)
	deps.Demo = true
	mux := http.NewServeMux()
	Mount(mux, deps, nil)
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/usage?tab=settings", nil))
	if response.Code != http.StatusOK {
		t.Fatalf("settings page = %d: %s", response.Code, response.Body.String())
	}
	requireOmits(t, response.Body.String(), `name="paidExcess"`, `name="initial_paidExcess"`)
	csrfResponse := httptest.NewRecorder()
	token := deps.CSRF.Token(csrfResponse, httptest.NewRequest(http.MethodGet, "/usage", nil))
	cookies := csrfResponse.Result().Cookies()
	before, err := deps.Store.SnapshotAs(t.Context(), principal)
	if err != nil {
		t.Fatal(err)
	}
	values := url.Values{"spendCap": {"8.40"}, "initial_spendCap": {"8.40"}, "_csrf": {token}}
	for _, edited := range []bool{false, true} {
		if edited {
			values.Set("spendCap", "9.00")
		}
		request := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/usage/spending-controls", strings.NewReader(values.Encode()))
		request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		for _, cookie := range cookies {
			request.AddCookie(cookie)
		}
		response := httptest.NewRecorder()
		mux.ServeHTTP(response, request)
		if response.Code != http.StatusSeeOther || !strings.Contains(response.Header().Get("Location"), "saved=ok") {
			t.Fatalf("save = %d: %s", response.Code, response.Body.String())
		}
		after, err := deps.Store.SnapshotAs(t.Context(), principal)
		if err != nil {
			t.Fatal(err)
		}
		if !edited {
			if !reflect.DeepEqual(before, after) {
				t.Fatal("saving untouched internal controls changed state or added an audit event")
			}
			continue
		}
		org := after.Organizations[principal.OrganizationID]
		if org.Billing.SpendCapCents != 900 || !org.Billing.PaidExcessEnabled || len(after.AuditEvents) != len(before.AuditEvents)+1 || after.AuditEvents[len(after.AuditEvents)-1].Action != "usage.spending_controls_updated" {
			t.Fatalf("edited internal cap was not saved and audited while preserving excess: %+v", after)
		}
	}
}

func TestInternalBillingPages(t *testing.T) {
	for _, state := range []string{"metered", "missing-project", "missing-token", "missing-provider", "commercial"} {
		t.Run(state, func(t *testing.T) {
			service, principal, meter := internalUsageFixture(t, memstore.New())
			deps := service.deps
			deps.Sessions = reviewSessions{principal: principal}
			deps.Render = viewRenderer{}
			deps.Tools = core.NewTools(deps.Store)
			deps.Demo = true
			if state == "missing-project" {
				_, selectedID := (core.Project{ID: principal.ProjectID}).TerraBaseIDs(principal.OrganizationID)
				for id := range meter.values {
					if id != selectedID {
						meter.fail = id
						break
					}
				}
			}
			if state == "missing-provider" {
				deps.Usage = nil
			}
			if state == "commercial" {
				if err := deps.Store.UpdateAs(t.Context(), principal, func(account *core.Account) error {
					org := account.Organizations[principal.OrganizationID]
					org.PlanID = "09XCWRH05BWWBADSBPTPDMFA7D"
					org.Billing = core.Billing{PlanKey: "business", Interval: "monthly", Status: "active", CustomerID: "cus_ui", SubscriptionID: "sub_ui"}
					account.Organizations[org.ID] = org
					return nil
				}); err != nil {
					t.Fatal(err)
				}
			}
			before, err := deps.Store.SnapshotAs(t.Context(), principal)
			if err != nil {
				t.Fatal(err)
			}
			mux := http.NewServeMux()
			Mount(mux, deps, nil)
			mux.HandleFunc("GET /organization", OrganizationBilling(deps, nil, func(_ core.Page, content templ.Component) templ.Component { return content }))
			for _, path := range []string{"/usage", "/organization?tab=billing&review=1&plan=pro&interval=monthly"} {
				t.Run(path, func(t *testing.T) {
					request := httptest.NewRequest(http.MethodGet, path, nil)
					if state != "missing-token" {
						request = request.WithContext(core.WithUserToken(request.Context(), func() (string, bool) { return "user-token", true }))
					}
					response := httptest.NewRecorder()
					mux.ServeHTTP(response, request)
					if response.Code != http.StatusOK {
						t.Fatalf("GET %s = %d: %s", path, response.Code, response.Body.String())
					}
					html := response.Body.String()
					if state == "commercial" {
						requireOmits(t, html, `id="internal-bill"`)
						if strings.HasPrefix(path, "/organization") {
							requireContains(t, html, "Change plan", `href="/billing/portal"`, `action="/billing/change"`, `id="review-billing-plan"`)
						}
						return
					}
					requireContains(t, html, `id="internal-bill"`, "Internal computed bill", "Reads are free.", "No Stripe charges.")
					requireOmits(t, html, `href="/billing/portal"`, `action="/billing/checkout"`, `action="/billing/change"`, `action="/billing/change/undo"`, `id="change-billing-plan"`, `id="review-billing-plan"`)
					if strings.HasPrefix(path, "/organization") {
						requireContains(t, html, "Internal reporting only.", "Internal billing reports usage without Stripe charges.")
						requireOmits(t, html, "Available plans", `aria-label="Billing interval"`, "No paid plans are available", "Only the organization owner can manage billing.")
					}
					if state == "metered" {
						requireContains(t, html, "US$ 5.00", "42 / 1200006", "US$ 1.20", "US$ 2.20", "Total estimate", "US$ 8.40", "Report-only spending cap reached", "services remain active")
						requireOmits(t, html, "Internal bill unavailable")
					} else {
						requireContains(t, html, "Internal bill unavailable", "Every project&#39;s monthly usage is required")
						requireOmits(t, html, "Total estimate", "Report-only spending cap reached", "US$ 0.00")
					}
				})
			}
			after, err := deps.Store.SnapshotAs(t.Context(), principal)
			if err != nil {
				t.Fatal(err)
			}
			if !reflect.DeepEqual(before, after) {
				t.Fatal("rendering billing pages changed account state")
			}
		})
	}
}
