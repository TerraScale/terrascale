package usage

import (
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/demovisuals"
	"terrascale.tech/dashboard/internal/platform/render"
)

func TestParseCentsAndCapFormsRejectAmbiguousOrOverflowingValues(t *testing.T) {
	for input, want := range map[string]int64{"0": 0, "12": 1200, "12.3": 1230, "12.34": 1234} {
		got, ok := parseCents(input)
		if !ok || got != want {
			t.Errorf("parseCents(%q) = (%d, %t), want (%d, true)", input, got, ok, want)
		}
	}
	for _, input := range []string{"-1", "+1", "1.234", "1e3", "92233720368547759", "1.00.0"} {
		if got, ok := parseCents(input); ok {
			t.Errorf("parseCents(%q) = (%d, true), want invalid", input, got)
		}
	}
	for _, values := range []url.Values{{"spendCap": {"10", "20"}}} {
		if _, _, ok := formCap(values, "spendCap"); ok {
			t.Errorf("formCap(%v) unexpectedly accepted ambiguous values", values)
		}
	}
	if cents, found, valid := formCap(url.Values{"spendCap": {""}}, "spendCap"); !found || !valid || cents != 0 {
		t.Fatalf("empty optional cap = (%d, %t, %t), want (0, true, true)", cents, found, valid)
	}
	if _, valid := capsFromForm(url.Values{"dbCap_db1": {"1", "2"}}, "dbCap_"); valid {
		t.Fatal("capsFromForm accepted a duplicate cap value")
	}
}

func TestSpendingControlsPersistForOwnerAndRejectViewerWithStaleAdminRole(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{ID: "user-1", Name: "Owner", Email: "owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	principal := account.Principal()
	project := account.Organizations[principal.OrganizationID].Projects[principal.ProjectID]
	database := core.Database{ID: "db-1", ProjectID: project.ID, Name: "primary", Status: "ready"}
	if err := store.Update(account.ID, func(current *core.Account) error {
		org := current.Organizations[principal.OrganizationID]
		project := org.Projects[principal.ProjectID]
		project.Databases[database.ID] = database
		org.Projects[project.ID] = project
		current.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}

	mux := http.NewServeMux()
	deps := core.Deps{Store: store, Sessions: usageTestSessions{principal}, CSRF: usageTestCSRF{}, Render: usageTestRenderer{}}
	Mount(mux, deps)
	form := url.Values{
		"spendCap": {"45.67"}, "paidExcess": {"true"},
		"projCap_" + project.ID: {"20"}, "dbCap_" + database.ID: {"7.5"},
	}
	request := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/usage/spending-controls", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusSeeOther {
		t.Fatalf("owner update returned %d: %s", response.Code, response.Body.String())
	}
	updated, err := store.SnapshotAs(principal)
	if err != nil {
		t.Fatal(err)
	}
	updatedOrg := updated.Organizations[principal.OrganizationID]
	updatedProject := updatedOrg.Projects[project.ID]
	if updatedOrg.Billing.SpendCapCents != 4567 || !updatedOrg.Billing.PaidExcessEnabled || updatedProject.SpendCapCents != 2000 || updatedProject.Databases[database.ID].SpendCapCents != 750 {
		t.Fatalf("spending controls not persisted: org=%+v project=%+v", updatedOrg.Billing, updatedProject)
	}

	viewer, err := store.CreateAccount(core.User{ID: "user-2", Name: "Viewer", Email: "viewer@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Update(account.ID, func(current *core.Account) error {
		org := current.Organizations[principal.OrganizationID]
		org.Members[viewer.User.ID] = core.Member{UserID: viewer.User.ID, Email: viewer.User.Email, Name: viewer.User.Name, Role: "viewer", Status: "active"}
		current.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	ownerSnapshot, found := store.Snapshot(account.ID)
	if !found {
		t.Fatal("owner account disappeared")
	}
	sharedOrg := ownerSnapshot.Organizations[principal.OrganizationID]
	if err := store.Update(viewer.ID, func(current *core.Account) error {
		current.Organizations[sharedOrg.ID] = sharedOrg
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	viewerPrincipal := core.Principal{
		UserID: viewer.User.ID, AccountID: viewer.ID, OrganizationID: principal.OrganizationID,
		ProjectID: principal.ProjectID, Role: "admin",
	}
	viewerMux := http.NewServeMux()
	viewerDeps := core.Deps{Store: store, Sessions: usageTestSessions{viewerPrincipal}, CSRF: usageTestCSRF{}, Render: usageTestRenderer{}}
	Mount(viewerMux, viewerDeps)
	request = httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/usage/spending-controls", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response = httptest.NewRecorder()
	viewerMux.ServeHTTP(response, request)
	if response.Code != http.StatusForbidden {
		t.Fatalf("viewer with stale admin principal role returned %d, want forbidden", response.Code)
	}
	latest, found := store.Snapshot(account.ID)
	if !found {
		t.Fatal("updated account disappeared")
	}
	if latest.Organizations[principal.OrganizationID].Billing.SpendCapCents != 4567 {
		t.Fatal("viewer update changed the organization spending cap")
	}
}

func TestAddUsageDoesNotOverflow(t *testing.T) {
	const maxInt64 = int64(^uint64(0) >> 1)
	total := maxInt64 - 1
	if addUsage(&total, 1) != true || total != maxInt64 {
		t.Fatalf("last representable addition failed: total=%d", total)
	}
	if addUsage(&total, 1) {
		t.Fatal("overflowing usage addition was accepted")
	}
	if addUsage(&total, -1) {
		t.Fatal("negative usage addition was accepted")
	}
}

func TestInvoiceFiltersUseReferenceDateRangesAndPageTenRows(t *testing.T) {
	now := time.Date(2026, time.September, 25, 12, 0, 0, 0, time.UTC)
	invoices := make([]invoiceRow, 0, 14)
	for i := range 13 {
		invoices = append(invoices, invoiceRow{
			Number: fmt.Sprintf("INV-%02d", i+1), Status: "paid", CreatedAt: now.AddDate(0, 0, -i),
		})
	}
	invoices = append(invoices, invoiceRow{
		Number: "INV-14", Status: "void", CreatedAt: time.Date(2025, time.December, 15, 12, 0, 0, 0, time.UTC),
	})

	page := makeInvoicePage(invoices, "paid", "3m", "2", now)
	if page.Total != 13 || page.PageCount != 2 || page.Page != 2 || page.First != 11 || len(page.Rows) != 3 {
		t.Fatalf("filtered page = total %d, pages %d, page %d, first %d, rows %d; want 13, 2, 2, 11, 3", page.Total, page.PageCount, page.Page, page.First, len(page.Rows))
	}
	if page.Rows[0].Number != "INV-11" || page.Rows[2].Number != "INV-13" {
		t.Fatalf("second page rows start/end at %q/%q, want INV-11/INV-13", page.Rows[0].Number, page.Rows[2].Number)
	}

	lastYear := makeInvoicePage(invoices, "void", "2025", "invalid", now)
	if lastYear.Total != 1 || lastYear.Page != 1 || lastYear.Rows[0].Number != "INV-14" {
		t.Fatalf("last-year void filter = %#v, want only INV-14 on page 1", lastYear)
	}

	boundary := []invoiceRow{
		{Number: "AT-CUTOFF", Status: "paid", CreatedAt: now.AddDate(0, -3, 0)},
		{Number: "BEFORE-CUTOFF", Status: "paid", CreatedAt: now.AddDate(0, -3, -1)},
	}
	threeMonths := makeInvoicePage(boundary, "", "3m", "", now)
	if threeMonths.Total != 1 || threeMonths.Rows[0].Number != "AT-CUTOFF" {
		t.Fatalf("three-month lower-bound filter = %#v, want exact cutoff only", threeMonths)
	}

	invalidFilters := makeInvoicePage(invoices, "unrecognized", "1900", "-9", now)
	if invalidFilters.Status != "" || invalidFilters.Issued != "" || invalidFilters.Page != 1 {
		t.Fatalf("invalid filters were not normalized: status=%q issued=%q page=%d", invalidFilters.Status, invalidFilters.Issued, invalidFilters.Page)
	}
}

func TestInvoicePagerEscapesAndPreservesBillingScope(t *testing.T) {
	page := invoicePage{Status: "paid", Issued: "3m"}
	href := page.Href("project & ?/one", 2)
	parsed, err := url.Parse(href)
	if err != nil {
		t.Fatalf("parse invoice page href %q: %v", href, err)
	}
	query := parsed.Query()
	if parsed.Path != "/usage" || query.Get("tab") != "billing" || query.Get("project") != "project & ?/one" || query.Get("status") != "paid" || query.Get("issued") != "3m" || query.Get("page") != "2" {
		t.Fatalf("invoice page href lost or changed scope: %q -> %#v", href, query)
	}
}

func TestInvoiceLinksAcceptOnlyAbsoluteHTTPSURLs(t *testing.T) {
	for _, test := range []struct {
		value string
		want  string
	}{
		{value: "https://invoice.stripe.com/inv_123", want: "https://invoice.stripe.com/inv_123"},
		{value: "http://invoice.stripe.com/inv_123"},
		{value: "//invoice.stripe.com/inv_123"},
		{value: "javascript:alert(1)"},
		{value: "https://user:secret@invoice.stripe.com/inv_123"},
		{value: "https:///missing-host"},
	} {
		if got := httpsInvoiceURL(test.value); got != test.want {
			t.Errorf("httpsInvoiceURL(%q) = %q, want %q", test.value, got, test.want)
		}
	}
}

func TestUsageInvoiceBillingFiltersAreOrganizationScopedAndPortalIsOwnerOnly(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{
		ID: "invoice-owner", Name: "Invoice Owner", Email: "invoice-owner@example.test", PasswordHash: "unused",
	})
	if err != nil {
		t.Fatal(err)
	}
	principal := account.Principal()
	base := time.Now().UTC().Truncate(time.Second)
	invoices := make([]core.Invoice, 0, 14)
	for i := range 13 {
		createdAt := base.AddDate(0, 0, -i)
		number := fmt.Sprintf("INV-%02d", i+1)
		invoices = append(invoices, core.Invoice{
			ID: number, Number: number, Status: "paid", AmountCents: int64((i + 1) * 125), Currency: "usd",
			CreatedAt: createdAt, PeriodStart: createdAt.AddDate(0, -1, 0), PeriodEnd: createdAt,
			HostedURL: "https://invoice.stripe.com/" + number, PDFURL: "https://files.stripe.com/" + number + ".pdf",
		})
	}
	invoices = append(invoices, core.Invoice{
		ID: "INV-14", Number: "INV-14", Status: "void", AmountCents: 1000, Currency: "usd",
		CreatedAt: time.Date(base.Year()-1, time.December, 15, 12, 0, 0, 0, time.UTC),
		HostedURL: "http://invoice.stripe.com/unsafe", PDFURL: "javascript:alert(1)",
	})

	otherOrganization := account.Organizations[principal.OrganizationID]
	otherOrganization.ID = "org-unrelated-invoice-scope"
	otherOrganization.Name = "Unrelated organization"
	otherOrganization.Projects = make(map[string]core.Project)
	otherOrganization.Billing = core.Billing{Invoices: []core.Invoice{{ID: "foreign-invoice", Number: "FOREIGN-INVOICE", Status: "paid", CreatedAt: base}}}

	if err := store.Update(account.ID, func(current *core.Account) error {
		organization := current.Organizations[principal.OrganizationID]
		organization.Billing.Invoices = invoices
		current.Organizations[organization.ID] = organization
		current.Organizations[otherOrganization.ID] = otherOrganization
		return nil
	}); err != nil {
		t.Fatal(err)
	}

	csrf := usageTestCSRF{}
	newMux := func() *http.ServeMux {
		mux := http.NewServeMux()
		Mount(mux, core.Deps{
			Store: store, Sessions: usageTestSessions{principal}, CSRF: csrf, Render: render.New(store, csrf),
		})
		return mux
	}
	requestPath := "/usage?" + url.Values{
		"tab": {"billing"}, "project": {principal.ProjectID}, "status": {"paid"}, "issued": {"3m"}, "page": {"2"},
	}.Encode()
	response := httptest.NewRecorder()
	newMux().ServeHTTP(response, httptest.NewRequest(http.MethodGet, requestPath, nil))
	if response.Code != http.StatusOK {
		t.Fatalf("owner invoice page returned %d: %s", response.Code, response.Body.String())
	}
	body := response.Body.String()
	for _, expected := range []string{
		"Manage subscription", `href="/billing/portal"`,
		"Invoice status", "Last 3 months", "Showing 11–13 of 13",
		"INV-11", "INV-12", "INV-13", "Period", "https://invoice.stripe.com/INV-11", "https://files.stripe.com/INV-11.pdf",
	} {
		if !strings.Contains(body, expected) {
			t.Errorf("owner invoice page is missing %q", expected)
		}
	}
	for _, unexpected := range []string{"INV-01", "INV-10", "INV-14", "FOREIGN-INVOICE", "http://invoice.stripe.com/unsafe", "javascript:alert(1)"} {
		if strings.Contains(body, unexpected) {
			t.Errorf("owner invoice page unexpectedly contains %q", unexpected)
		}
	}
	if !strings.Contains(body, "project="+url.QueryEscape(principal.ProjectID)) || !strings.Contains(body, "issued=3m") || !strings.Contains(body, "status=paid") {
		t.Errorf("invoice pager did not preserve project and filters: %s", body)
	}

	if err := store.Update(account.ID, func(current *core.Account) error {
		organization := current.Organizations[principal.OrganizationID]
		organization.OwnerUserID = "different-owner"
		current.Organizations[organization.ID] = organization
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	response = httptest.NewRecorder()
	newMux().ServeHTTP(response, httptest.NewRequest(http.MethodGet, requestPath, nil))
	if response.Code != http.StatusOK {
		t.Fatalf("non-owner invoice page returned %d: %s", response.Code, response.Body.String())
	}
	body = response.Body.String()
	if strings.Contains(body, `href="/billing/portal"`) || !strings.Contains(body, "Only the organization owner can manage billing.") {
		t.Fatalf("non-owner billing controls = %q, want read-only owner notice and no portal link", body)
	}
}

func TestUsageOverviewShowsConfiguredAllowanceAndUnavailableWorkloadHonestly(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{
		ID: "usage-overview-owner", Name: "Usage Owner", Email: "usage-overview@example.test", PasswordHash: "unused", PlatformAdmin: true,
	})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	plan := core.Plan{
		ID: "plan_growth_test", Key: "growth-test", Name: "Growth test", Visible: true,
		StorageMiB: 2048, MonthlyRequests: 120000,
	}
	if err := store.UpsertPlanAs(p, plan); err != nil {
		t.Fatalf("configure plan allowance: %v", err)
	}
	if err := store.Update(account.ID, func(current *core.Account) error {
		org := current.Organizations[p.OrganizationID]
		org.PlanID = plan.ID
		project := org.Projects[p.ProjectID]
		project.Region = "GRU"
		project.Databases["db-one"] = core.Database{
			ID: "db-one", ProjectID: project.ID, Name: "primary", Region: "GRU",
			Documents: map[string]map[string][]byte{"users": {"ada": []byte(`{"name":"Ada"}`)}},
		}
		project.Databases["db-secondary"] = core.Database{
			ID: "db-secondary", ProjectID: project.ID, Name: "secondary", Region: "GRU",
			Documents: map[string]map[string][]byte{"users": {
				"ben": []byte(`{"name":"Ben"}`), "cam": []byte(`{"name":"Cam"}`),
			}},
		}
		org.Projects[project.ID] = project
		org.Projects["project-other"] = core.Project{
			ID: "project-other", OrganizationID: org.ID, Name: "Other project", Region: "EWR",
			Databases: map[string]core.Database{
				"db-two":   {ID: "db-two", ProjectID: "project-other", Name: "secondary", Region: "EWR"},
				"db-three": {ID: "db-three", ProjectID: "project-other", Name: "archive", Region: "EWR"},
			},
		}
		current.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}

	csrf := usageTestCSRF{}
	mux := http.NewServeMux()
	Mount(mux, core.Deps{
		Store: store, Sessions: usageTestSessions{p}, CSRF: csrf, Render: render.New(store, csrf),
	})
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/usage", nil))
	if response.Code != http.StatusOK {
		t.Fatalf("usage page returned %d: %s", response.Code, response.Body.String())
	}
	body := response.Body.String()
	for _, expected := range []string{
		"Plan allowance", "Growth test", "2048 MiB", "120000 requests/month",
		"Workload history", "Workload history is unavailable.", "no timestamped workload source",
		"By project", "Other project", "2 databases", "Region: EWR", "Live usage provider is not configured.",
	} {
		if !strings.Contains(strings.ToLower(body), strings.ToLower(expected)) {
			t.Errorf("usage page is missing %q", expected)
		}
	}
	if strings.Contains(body, "<dt>Read operations</dt>") || strings.Contains(body, "Sample dashboard data") {
		t.Fatal("usage page presented unconfigured provider totals as measured usage")
	}

	response = httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/usage?database=db-one", nil))
	if response.Code != http.StatusOK {
		t.Fatalf("filtered usage page returned %d: %s", response.Code, response.Body.String())
	}
	assertUsageDatabaseFilter(t, response.Body.String(), "db-one", "primary", "secondary")
	if responseBody := response.Body.String(); usageMetricValue(responseBody, "Write units") != "" {
		t.Fatalf("unavailable provider rendered a write count: %s", responseBody)
	}

	demoMux := http.NewServeMux()
	Mount(demoMux, core.Deps{
		Store: store, Sessions: usageTestSessions{p}, CSRF: csrf, Render: render.New(store, csrf), Demo: true,
	})
	for _, test := range []struct {
		query       string
		selectedID  string
		selectedRow string
		otherRow    string
		writeCount  string
	}{
		{query: "database=db-one", selectedID: "db-one", selectedRow: "primary", otherRow: "secondary", writeCount: "1"},
		{query: "database=db-secondary", selectedID: "db-secondary", selectedRow: "secondary", otherRow: "primary", writeCount: "2"},
		{query: "database=db-two", selectedID: "", selectedRow: "primary", otherRow: "secondary", writeCount: "3"},
	} {
		t.Run(test.query, func(t *testing.T) {
			response := httptest.NewRecorder()
			demoMux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/usage?"+test.query, nil))
			if response.Code != http.StatusOK {
				t.Fatalf("demo usage page returned %d: %s", response.Code, response.Body.String())
			}
			body := response.Body.String()
			assertUsageDatabaseFilter(t, body, test.selectedID, test.selectedRow, test.otherRow)
			if got := usageMetricValue(body, "Write units"); got != test.writeCount {
				t.Errorf("demo write units = %q, want selected-scope document count %q", got, test.writeCount)
			}
			if strings.Contains(body, "Illustrative demo capacity") || strings.Contains(body, "ts-usage-chart-shell") {
				t.Fatal("ordinary demo account received seeded Mario charts")
			}
		})
	}
}

func TestMarioDemoUsageChartsAndRecordedBillingSummary(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{ID: demovisuals.MarioUserID, Name: "Mario Demo", Email: "mario-usage@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now().UTC()
	if err := store.Update(account.ID, func(current *core.Account) error {
		original := current.Organizations[current.ActiveOrganizationID]
		project := original.Projects[current.ActiveProjectID]
		organization := original
		organization.ID = demovisuals.MarioOrganizationID
		organization.Slug = "terrascale-labs"
		organization.Name = "TerraScale Labs"
		organization.PlanID = "plan_pro"
		project.OrganizationID = organization.ID
		project.Databases["db-core"] = core.Database{
			ID: "db-core", ProjectID: project.ID, Name: "core-primary",
			Documents: map[string]map[string][]byte{"orders": {"one": []byte(`{"status":"paid"}`)}},
		}
		other := core.Project{
			ID: "project-edge", OrganizationID: organization.ID, Name: "edge-analytics", Region: "FRA",
			Databases: map[string]core.Database{
				"db-edge": {ID: "db-edge", ProjectID: "project-edge", Name: "edge-events", Documents: map[string]map[string][]byte{"events": {"one": []byte(`{"event":"new"}`)}}},
			},
		}
		organization.Projects = map[string]core.Project{project.ID: project, other.ID: other}
		organization.Billing.Invoices = []core.Invoice{{
			Number: "INV-DEMO", Status: "open", AmountCents: 1234, Currency: "USD",
			PeriodStart: now.Add(-10 * 24 * time.Hour), PeriodEnd: now.Add(20 * 24 * time.Hour),
			CreatedAt: now.Add(2 * 24 * time.Hour),
		}}
		current.Organizations[organization.ID] = organization
		current.ActiveOrganizationID = organization.ID
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	account, _ = store.Snapshot(account.ID)
	csrf := usageTestCSRF{}
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: usageTestSessions{account.Principal()}, CSRF: csrf, Render: render.New(store, csrf), Demo: true})

	for _, rangeName := range []string{"24h", "7d", "30d"} {
		response := httptest.NewRecorder()
		mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/usage?range="+rangeName, nil))
		if response.Code != http.StatusOK {
			t.Fatalf("%s usage returned %d: %s", rangeName, response.Code, response.Body.String())
		}
		body := response.Body.String()
		for _, expected := range []string{
			"Illustrative demo capacity", "Sample storage", "Sample writes", "81%", "163%",
			"Sample organization operations", "ts-usage-chart-shell", "ts-cat-1", "ts-cat-2", "ts-cat-3",
			"By project", "core-primary", "edge-analytics", "Stored dashboard document bytes",
		} {
			if !strings.Contains(body, expected) {
				t.Errorf("%s usage missing %q", rangeName, expected)
			}
		}
		if strings.Contains(body, "8192 MiB</dd>") || strings.Contains(body, "5000000 requests/month</dd>") {
			t.Fatal("illustrative capacities replaced canonical plan allowances")
		}
	}
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/usage?project="+account.ActiveProjectID+"&range=30d", nil))
	if response.Code != http.StatusOK || !strings.Contains(response.Body.String(), "By database") || !strings.Contains(response.Body.String(), "core-primary") {
		t.Fatalf("project-scoped breakdown returned %d: %s", response.Code, response.Body.String())
	}
	response = httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/usage?tab=billing", nil))
	if response.Code != http.StatusOK {
		t.Fatalf("billing page returned %d: %s", response.Code, response.Body.String())
	}
	for _, expected := range []string{"Next recorded invoice", "$12.34", "Billing period", "Outstanding balance", "recorded invoice period"} {
		if !strings.Contains(response.Body.String(), expected) {
			t.Errorf("billing summary missing %q", expected)
		}
	}
}

func TestBillingSummaryDistinguishesMissingInvoicesFromZeroOpenBalance(t *testing.T) {
	now := time.Now().UTC()
	missing := summarizeBilling(nil, now)
	if missing.NextInvoice != "Not recorded" || missing.Period != "Unavailable" || missing.Outstanding != "Not recorded" || missing.HasPeriod {
		t.Fatalf("missing invoice summary = %#v", missing)
	}
	paid := summarizeBilling([]core.Invoice{{Status: "paid", AmountCents: 1200, Currency: "USD"}}, now)
	if paid.Outstanding != "0 open invoices" || paid.NextInvoice != "Not recorded" {
		t.Fatalf("paid-only invoice summary = %#v", paid)
	}
}

func assertUsageDatabaseFilter(t *testing.T, body, selectedID, selectedRow, otherRow string) {
	t.Helper()
	selectStart := strings.Index(body, `id="usage-database"`)
	if selectStart < 0 {
		t.Fatal("usage page is missing the database selector")
	}
	selectEnd := strings.Index(body[selectStart:], "</select>")
	if selectEnd < 0 {
		t.Fatal("usage database selector is not closed")
	}
	databaseOptions := body[selectStart : selectStart+selectEnd]
	for _, id := range []string{"db-one", "db-secondary"} {
		if !strings.Contains(databaseOptions, `value="`+id+`"`) {
			t.Errorf("database selector is missing selected-project option %q", id)
		}
	}
	if strings.Contains(databaseOptions, `value="db-two"`) {
		t.Error("database selector included an option from another project")
	}
	if selectedID != "" {
		optionStart := strings.Index(databaseOptions, `value="`+selectedID+`"`)
		if optionStart < 0 {
			t.Errorf("database selector did not mark %q as selected", selectedID)
		} else {
			optionEnd := strings.Index(databaseOptions[optionStart:], "</option>")
			if optionEnd < 0 || !strings.Contains(databaseOptions[optionStart:optionStart+optionEnd], "selected") {
				t.Errorf("database selector did not mark %q as selected", selectedID)
			}
		}
	} else if strings.Contains(databaseOptions, " selected") {
		t.Error("invalid foreign-project filter remained selected")
	}
	tableStart := strings.Index(body, `<table class="ts-table">`)
	if tableStart < 0 {
		t.Fatal("usage page is missing the database table")
	}
	tableEnd := strings.Index(body[tableStart:], "</table>")
	if tableEnd < 0 {
		t.Fatal("usage database table is not closed")
	}
	databaseTable := body[tableStart : tableStart+tableEnd]
	hasSelectedRow := strings.Contains(databaseTable, selectedRow)
	hasOtherRow := strings.Contains(databaseTable, otherRow)
	if selectedID == "" && (!hasSelectedRow || !hasOtherRow) {
		t.Errorf("invalid database filter did not fall back to all selected-project rows: %s", databaseTable)
	} else if selectedID != "" && (!hasSelectedRow || hasOtherRow) {
		t.Errorf("database table rows do not match the filter: %s", databaseTable)
	}
}

func usageMetricValue(body, label string) string {
	labelStart := strings.Index(body, "<dt>"+label+"</dt>")
	if labelStart < 0 {
		return ""
	}
	valueStart := strings.Index(body[labelStart:], "<dd>")
	if valueStart < 0 {
		return ""
	}
	valueStart += labelStart + len("<dd>")
	valueEnd := strings.Index(body[valueStart:], "</dd>")
	if valueEnd < 0 {
		return ""
	}
	return strings.TrimSpace(body[valueStart : valueStart+valueEnd])
}

type usageTestSessions struct{ principal core.Principal }

func (s usageTestSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error {
	return errors.New("unused")
}
func (s usageTestSessions) Principal(*http.Request) (core.Principal, bool) { return s.principal, true }
func (s usageTestSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return s.principal, true
}
func (usageTestSessions) End(http.ResponseWriter, *http.Request) {}
func (usageTestSessions) RevokeUser(string, string)              {}

type usageTestCSRF struct{}

func (usageTestCSRF) Token(http.ResponseWriter, *http.Request) string { return "csrf" }
func (usageTestCSRF) Verify(*http.Request) error                      { return nil }
func (usageTestCSRF) Protect(next http.Handler) http.Handler          { return next }

type usageTestRenderer struct{}

func (usageTestRenderer) Page(http.ResponseWriter, *http.Request, core.Page) error { return nil }
func (usageTestRenderer) Fragment(http.ResponseWriter, *http.Request, core.Fragment) error {
	return nil
}
