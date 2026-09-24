package subscriptions

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestSubscriptionPagesRenderTypedTemplViewsWithEscapingAndFinalCSRF(t *testing.T) {
	store, principal, sessionID := subscriptionPageFixture(t)
	renderer := &subscriptionPageRenderer{sessionID: sessionID}
	deps := core.Deps{
		Store: store, Sessions: subscriptionPageSessions{principal: principal},
		CSRF: subscriptionPageCSRF{}, Render: renderer, Demo: true,
	}
	mux := http.NewServeMux()
	Mount(mux, deps)

	for _, test := range []struct {
		name string
		path string
		want []string
		omit []string
	}{
		{
			name: "billing",
			path: "/billing",
			want: []string{"Plans and billing", "Billing &lt;img src=x onerror=alert(1)&gt;", "&lt;script&gt;", "Choose monthly", `value="renderer-token&lt;&amp;&#34;"`, `name="return" value="/billing"`},
			omit: []string{"<script>", `name="return" value="/subscriptions"`},
		},
		{
			name: "checkout",
			path: "",
			want: []string{"Local payment setup", "&lt;script&gt;", `value="renderer-token&lt;&amp;&#34;"`, "/complete", "/cancel"},
			omit: []string{"<script>"},
		},
		{
			name: "portal",
			path: "/billing/portal/local-test",
			want: []string{"Local billing portal", "Billing &lt;img src=x onerror=alert(1)&gt;"},
			omit: []string{"<img src=x"},
		},
	} {
		t.Run(test.name, func(t *testing.T) {
			path := test.path
			if path == "" {
				path = "/checkout/" + renderer.sessionID
			}
			response := httptest.NewRecorder()
			mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, path, nil))
			if response.Code != http.StatusOK {
				t.Fatalf("GET %s status/body = %d/%s", path, response.Code, response.Body.String())
			}
			body := response.Body.String()
			for _, want := range test.want {
				if !strings.Contains(body, want) {
					t.Errorf("GET %s body missing %q: %s", path, want, body)
				}
			}
			for _, forbidden := range test.omit {
				if strings.Contains(body, forbidden) {
					t.Errorf("GET %s body contains unescaped or unavailable content %q: %s", path, forbidden, body)
				}
			}
			if test.name == "billing" {
				teamCardStart := strings.Index(body, `aria-labelledby="plan-team-templ-test">`)
				if teamCardStart < 0 {
					t.Fatal("monthly-only team plan card is missing")
				}
				teamCard := body[teamCardStart:]
				if end := strings.Index(teamCard, "</article>"); end >= 0 {
					teamCard = teamCard[:end]
				}
				if strings.Contains(teamCard, "Choose annual") {
					t.Errorf("monthly-only team plan exposed annual checkout: %s", teamCard)
				}
			}
		})
	}

	oldRoute := httptest.NewRecorder()
	mux.ServeHTTP(oldRoute, httptest.NewRequest(http.MethodGet, "/subscriptions", nil))
	if oldRoute.Code != http.StatusNotFound {
		t.Fatalf("billing handler still owns /subscriptions: %d", oldRoute.Code)
	}
	if len(renderer.pages) != 3 {
		t.Fatalf("rendered page count = %d, want 3", len(renderer.pages))
	}
	for _, page := range renderer.pages {
		if page.View == nil || page.CSRFToken != `renderer-token<&"` || page.Nonce != "renderer-nonce" || page.Shell.OrganizationName != "canonical shell" {
			t.Errorf("renderer did not supply the canonical page context before invoking the view: %#v", page)
		}
	}
}

func subscriptionPageFixture(t *testing.T) (*core.Store, core.Principal, string) {
	t.Helper()
	store, principal := newBusinessAccount(t)
	if err := store.UpdateAs(principal, func(account *core.Account) error {
		org := account.Organizations[principal.OrganizationID]
		org.Name = "Billing <img src=x onerror=alert(1)>"
		account.Organizations[principal.OrganizationID] = org
		return nil
	}); err != nil {
		t.Fatalf("set unsafe organization name: %v", err)
	}
	admin, err := store.CreateAccount(core.User{
		Email: "subscription-admin@example.test", Name: "Subscription Admin", PasswordHash: "test-hash", PlatformAdmin: true,
	})
	if err != nil {
		t.Fatalf("CreateAccount(admin) error = %v", err)
	}
	if err := store.UpsertPlanAs(admin.Principal(), core.Plan{
		ID: "plan_team_templ_test", Key: "team-templ-test", Name: `<script>alert("x")</script> Team`,
		PriceCents: 2500, Visible: true, StorageMiB: 1024, MonthlyRequests: 50000,
	}); err != nil {
		t.Fatalf("UpsertPlanAs(custom plan) error = %v", err)
	}
	session, err := NewBillingService(store).BeginDemoCheckout(principal, "team-templ-test", intervalMonthly, "/billing")
	if err != nil {
		t.Fatalf("BeginDemoCheckout() error = %v", err)
	}
	return store, principal, session.ID
}

type subscriptionPageSessions struct{ principal core.Principal }

func (s subscriptionPageSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error {
	return nil
}
func (s subscriptionPageSessions) Principal(*http.Request) (core.Principal, bool) {
	return s.principal, true
}
func (s subscriptionPageSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return s.principal, false
}
func (subscriptionPageSessions) End(http.ResponseWriter, *http.Request) {}
func (subscriptionPageSessions) RevokeUser(string, string)              {}

type subscriptionPageCSRF struct{}

func (subscriptionPageCSRF) Token(http.ResponseWriter, *http.Request) string {
	return "unused-handler-token"
}
func (subscriptionPageCSRF) Verify(*http.Request) error                { return nil }
func (subscriptionPageCSRF) Protect(handler http.Handler) http.Handler { return handler }

type subscriptionPageRenderer struct {
	pages     []core.Page
	sessionID string
}

func (renderer *subscriptionPageRenderer) Page(w http.ResponseWriter, r *http.Request, page core.Page) error {
	if page.View == nil {
		return core.ErrInvalid
	}
	page.CSRFToken = `renderer-token<&"`
	page.Nonce = "renderer-nonce"
	page.Shell = core.Shell{OrganizationName: "canonical shell"}
	renderer.pages = append(renderer.pages, page)
	return page.View(page).Render(r.Context(), w)
}

func (*subscriptionPageRenderer) Fragment(http.ResponseWriter, *http.Request, core.Fragment) error {
	return nil
}
