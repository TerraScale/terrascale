package app

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"os/exec"
	"reflect"
	"strings"
	"sync/atomic"
	"testing"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/auth"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/platform/config"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	"terrascale.tech/sdk/v1"
)

func TestPersistentDemoIsolatesLiveStripe(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	// Proxy settings must precede package initialization: Go caches them.
	// Run the same race-instrumented test binary with a refusing local proxy.
	if os.Getenv("TS_DEMO_ISOLATION_CHILD") == "" {
		// Prepare before blocking downloads, retaining the harness's skip/error handling.
		terrabasetest.Start(t)
		var providerCalls atomic.Int64
		provider := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			providerCalls.Add(1)
			http.Error(w, "local proxy refuses provider connections", http.StatusForbidden)
		}))
		t.Cleanup(provider.Close)
		for _, name := range []string{"HTTPS_PROXY", "HTTP_PROXY", "https_proxy", "http_proxy"} {
			t.Setenv(name, provider.URL)
		}
		t.Setenv("NO_PROXY", "127.0.0.1,localhost")
		t.Setenv("no_proxy", "127.0.0.1,localhost")
		t.Setenv("TS_DEMO_ISOLATION_CHILD", "1")
		command := exec.CommandContext(t.Context(), os.Args[0], "-test.run=^TestPersistentDemoIsolatesLiveStripe$", "-test.count=1", "-test.parallel=1")
		if output, err := command.CombinedOutput(); err != nil {
			t.Errorf("isolated regression: %v\n%s", err, output)
		}
		if calls := providerCalls.Load(); calls != 0 {
			t.Errorf("Stripe provider calls = %d, want zero", calls)
		}
		return
	}
	tb := terrabasetest.Start(t)
	db, key := terrabasetest.Database(t, tb, "demo-isolation")
	if err := terrabase.EnsureCollections(t.Context(), db); err != nil {
		t.Fatal(err)
	}
	store := terrabase.New(db)
	if err := store.SeedPlans(t.Context()); err != nil {
		t.Fatal(err)
	}
	password := "local-fixture-password"
	hash, err := auth.HashPassword(password)
	if err != nil {
		t.Fatal(err)
	}
	// Plain fixture ULIDs avoid the memory account factory's prefixed IDs.
	user := core.User{ID: "01ARZ3NDEKTSV4RRFFQ69G5FAV", Name: "Owner", Email: "isolation@example.test", PasswordHash: hash, Role: "owner"}
	org := core.Organization{
		ID: "01ARZ3NDEKTSV4RRFFQ69G5FAW", Name: "Isolation", Kind: "personal", Status: "active",
		OwnerUserID: user.ID, PlanID: core.DefaultPlans()[0].ID, BillingStatus: "active",
		Members:  map[string]core.Member{user.ID: {UserID: user.ID, Name: user.Name, Email: user.Email, Role: "owner", Status: "active"}},
		Projects: map[string]core.Project{}, Invitations: map[string]core.Invitation{}, CheckoutSessions: map[string]core.CheckoutSession{},
	}
	org.Projects[db.Project()] = core.Project{ID: db.Project(), OrganizationID: org.ID, Name: "Fixture", Status: "active"}
	account := core.Account{
		ID: "01ARZ3NDEKTSV4RRFFQ69G5FAX", User: user, Organizations: map[string]core.Organization{org.ID: org},
		ActiveOrganizationID: org.ID, ActiveProjectID: db.Project(), OnboardingCompleted: true,
	}
	if err := store.InsertAccount(t.Context(), account); err != nil {
		t.Fatal(err)
	}

	for name, value := range map[string]string{
		"STRIPE_MODE": payments.ModeLive, "STRIPE_SECRET_KEY": "sk_live_synthetic_isolation",
		"STRIPE_WEBHOOK_SECRET": "whsec_synthetic_isolation", "DASHBOARD_PUBLIC_ORIGIN": "https://dashboard.example.test",
		"TS_BILLING_TENANT_ID": "terrascale-internal", "TS_BILLING_PROJECT_ID": db.Project(), "TS_BILLING_DATABASE_ID": db.Name(),
	} {
		t.Setenv(name, value)
	}
	for _, plan := range []string{payments.PlanStarter, payments.PlanPro, payments.PlanBusiness} {
		for _, interval := range []string{payments.IntervalMonthly, payments.IntervalAnnual} {
			t.Setenv(payments.PriceEnvName(plan, interval), "price_synthetic_"+plan+"_"+interval)
		}
	}
	if _, err := payments.LoadStripeConfig(os.Getenv); err != nil {
		t.Fatalf("synthetic live config must be complete: %v", err)
	}
	cfg := config.Config{
		Demo: true, TerraBaseURL: tb.URL, TerraBaseAPIKey: key, TerraBaseProjectID: db.Project(),
		TerraBaseDatabase: db.Name(), TerraBaseHTTPClient: tb.Client, CSRFSecret: []byte(strings.Repeat("i", 32)),
	}
	application, err := New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	var cookies []*http.Cookie
	request := func(app *Application, method, path string, form url.Values) *httptest.ResponseRecorder {
		t.Helper()
		r := httptest.NewRequest(method, path, strings.NewReader(form.Encode()))
		if form != nil {
			r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		}
		for _, cookie := range cookies {
			r.AddCookie(cookie)
		}
		w := httptest.NewRecorder()
		app.Handler.ServeHTTP(w, r)
		return w
	}
	loginPage := request(application, http.MethodGet, "/login", nil)
	if loginPage.Code != http.StatusOK || !strings.Contains(loginPage.Body.String(), `type="password"`) {
		t.Fatalf("native login page = %d", loginPage.Code)
	}
	cookies = loginPage.Result().Cookies()
	if len(cookies) != 1 {
		t.Fatalf("login page cookies = %d, want CSRF cookie", len(cookies))
	}
	csrfToken := cookies[0].Value
	login := request(application, http.MethodPost, "/login", url.Values{"email": {user.Email}, "password": {password}, "_csrf": {csrfToken}})
	if login.Code != http.StatusSeeOther || login.Header().Get("Location") != "/" || len(login.Result().Cookies()) != 1 {
		t.Fatalf("native login = %d, location %q", login.Code, login.Header().Get("Location"))
	}
	cookies = append(cookies, login.Result().Cookies()...)
	restarted, err := New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	profile := request(restarted, http.MethodGet, "/profile", nil)
	if profile.Code != http.StatusOK || profile.Header().Get("Location") != "" || !strings.Contains(profile.Body.String(), user.Name) || !strings.Contains(profile.Body.String(), user.Email) {
		t.Fatalf("second app cannot read persisted account/session: %d, location %q", profile.Code, profile.Header().Get("Location"))
	}
	readState := func() map[string][]v1.Document {
		t.Helper()
		state := make(map[string][]v1.Document)
		for _, collection := range []string{"organizations", "billing", "checkout_sessions", "stripe_customers", "stripe_organizations"} {
			page, err := db.Collection(collection).List(t.Context(), v1.ListOptions{Limit: 200})
			if err != nil {
				t.Fatal(err)
			}
			state[collection] = page.Items
		}
		return state
	}
	before := readState()
	for _, tc := range []struct {
		method, path string
		form         url.Values
		status       int
	}{
		{http.MethodPost, payments.WebhookPath, nil, http.StatusServiceUnavailable},
		{http.MethodGet, "/checkout/" + db.Project(), nil, http.StatusNotFound},
		{http.MethodPost, "/checkout/" + db.Project() + "/complete", url.Values{"_csrf": {csrfToken}}, http.StatusNotFound},
		{http.MethodPost, "/billing/checkout", url.Values{"_csrf": {csrfToken}, "plan": {payments.PlanStarter}, "interval": {payments.IntervalMonthly}, "return": {"/onboarding"}}, http.StatusServiceUnavailable},
	} {
		response := request(restarted, tc.method, tc.path, tc.form)
		if response.Code != tc.status {
			t.Errorf("%s %s = %d, want %d", tc.method, tc.path, response.Code, tc.status)
		}
	}
	if after := readState(); !reflect.DeepEqual(before, after) {
		t.Error("rejected billing requests changed persisted checkout/entitlement documents or versions")
	}
}
