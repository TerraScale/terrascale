package e2e

import (
	"fmt"
	"net/url"
	"regexp"
	"strings"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

const newOrganizationPath = "/api/v1/dashboard/organization/new-organization"

func TestCutoverBusinessOrganizationRetriesCanceledAnnualCheckoutAsMonthly(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("cutover-organization"))
	goToOK(t, page, "/organization?newOrganization=organization")
	wizard := page.Locator("dialog#create-organization")
	visible(t, wizard)

	organizationName := uniqueName("Cutover business")
	organization := wizardStep(page, wizard, "organization")
	fill(t, organization.Locator(`[name="name"]`), organizationName)
	submitAndExpect(t, page, newOrganizationPath, 303, byRole(organization, *playwright.AriaRoleButton, "Create organization", true).Click)
	visible(t, byRole(wizard, *playwright.AriaRoleHeading, "Set up billing"))

	payment := wizardStep(page, wizard, "payment-method")
	plans := payment.Locator(".ts-org-plan")
	must(t, expect.Locator(plans).ToHaveCount(3))
	for _, text := range []string{"Starter", "$5.00", "5 GB-region storage", "1M operations/month", "5 databases", "5 team members", "30-day free trial"} {
		must(t, expect.Locator(plans.First()).ToContainText(text))
	}
	must(t, expect.Locator(plans.Last()).ToContainText("Business"))
	must(t, expect.Locator(payment).ToContainText("local checkout simulation"))
	screenshot(t, page, "organization-billing-step")
	must(t, payment.Locator(`input[name="plan"][value="starter"]`).Check())
	must(t, payment.Locator(`input[name="interval"][value="annual"]`).Check())
	submitAndExpect(t, page, newOrganizationPath, 307, byRole(payment, *playwright.AriaRoleButton, "Continue to checkout").Click)
	must(t, page.WaitForURL(regexp.MustCompile(`/checkout/cs_local_`)))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Local payment setup"))
	must(t, expect.Locator(page.Locator(".ts-auth-subtitle")).ToContainText("Starter · billed annually"))
	must(t, expect.Locator(page.GetByRole(*playwright.AriaRoleStatus)).ToContainText("No card details are collected"))
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Cancel checkout"))

	waitForPath(t, page, func(u *url.URL) bool {
		return u.Path == "/organization" && u.Query().Get("payment") == "canceled"
	})
	must(t, expect.Locator(wizard.GetByRole(*playwright.AriaRoleAlert)).ToContainText("Payment setup canceled"))
	visible(t, byRole(wizard, *playwright.AriaRoleHeading, "Set up billing"))
	must(t, payment.Locator(`input[name="interval"][value="monthly"]`).Check())
	submitAndExpect(t, page, newOrganizationPath, 307, byRole(payment, *playwright.AriaRoleButton, "Continue to checkout").Click)
	must(t, page.WaitForURL(regexp.MustCompile(`/checkout/cs_local_`)))
	must(t, expect.Locator(page.Locator(".ts-auth-subtitle")).ToContainText("billed monthly"))
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Confirm local demo setup"))

	waitForPath(t, page, func(u *url.URL) bool {
		return u.Path == "/organization" && u.Query().Get("payment") == "success"
	})
	project := wizardStep(page, wizard, "project")
	visible(t, project)
	projectName := uniqueName("Cutover project")
	fill(t, project.Locator(`[name="name"]`), projectName)
	_, err := project.Locator(`[name="regions"]`).SelectOption(playwright.SelectOptionValues{Values: &[]string{"GRU", "SFO"}})
	must(t, err)
	selectOption(t, project.Locator(`[name="region"]`), "GRU")
	selectOption(t, project.Locator(`[name="backupRegion"]`), "SFO")
	submitAndExpect(t, page, newOrganizationPath, 303, byRole(project, *playwright.AriaRoleButton, "Create project", true).Click)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/organization" && u.RawQuery == "" })
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, organizationName))
	visible(t, page.Locator("#projects tbody tr").Filter(playwright.LocatorFilterOptions{HasText: projectName}))
}

func TestCutoverOwnerCreatesAndRevokesAPIKeyThenInvitesMember(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("cutover-credentials"))
	goToOK(t, page, "/api-keys?create=key")
	keyName := uniqueName("cutover-key")
	createKey := page.Locator(`form[action="/api/v1/dashboard/credentials/key"]`)
	fill(t, createKey.Locator(`[name="name"]`), keyName)
	fill(t, createKey.Locator(`[name="scopes"]`), "database:read")
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/key", 200, byRole(createKey, *playwright.AriaRoleButton, "Create key").Click)
	secret := func() playwright.Locator {
		return page.Locator(`section[role="status"]`).Filter(playwright.LocatorFilterOptions{HasText: "Secret for " + keyName})
	}
	must(t, expect.Locator(secret().Locator("code")).ToContainText(regexp.MustCompile(`^tsk_demo_`)))
	keyRow := page.Locator("table.ts-table tbody tr").Filter(playwright.LocatorFilterOptions{HasText: keyName})
	must(t, expect.Locator(keyRow).ToContainText("Active"))
	click(t, byRole(keyRow, *playwright.AriaRoleButton, "Revoke"))
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/revoke", 303,
		byRole(page.Locator("dialog[open]"), *playwright.AriaRoleButton, "Revoke credential").Click)
	must(t, expect.Locator(keyRow).ToContainText("Revoked"))
	must(t, expect.Locator(secret()).ToHaveCount(0))

	goToOK(t, page, "/access")
	invitee := newAccount("cutover-invitee").Email
	invite := page.Locator(`form.ts-access-overview-invite[action="/api/v1/access/invitations"]`)
	fill(t, invite.Locator(`input[name="email"]`), invitee)
	submitAndExpect(t, page, "/api/v1/access/invitations", 200, byRole(invite, *playwright.AriaRoleButton, "Send invite").Click)
	invitation := page.Locator(`section[aria-labelledby="invitation-link-title"]`)
	must(t, expect.Locator(invitation).ToContainText("Invitation created"))
	must(t, expect.Locator(invitation.Locator(`a[href^="/invitations/"]`)).ToHaveCount(1))
	must(t, expect.Locator(page.Locator(".ts-access-invitation-summary")).ToContainText(invitee))
}

func TestCutoverProfileSettingsPersistAndPasswordChangeEndsSession(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	account := newAccount("cutover-profile")
	registerAndOnboard(t, page, account)
	goToOK(t, page, "/profile?tab=profile")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Roles and permissions"))
	visible(t, page.GetByText("Access selected project", playwright.PageGetByTextOptions{Exact: new(true)}))
	newEmail := newAccount("cutover-renamed").Email
	email := page.Locator(`form[action="/api/v1/dashboard/profile/email-change"]`)
	fill(t, email.Locator(`[name="newEmail"]`), newEmail)
	fill(t, email.Locator(`[name="password"]`), account.Password)
	submitAndExpect(t, page, "/api/v1/dashboard/profile/email-change", 303, byRole(email, *playwright.AriaRoleButton, "Change email").Click)
	must(t, expect.Locator(page.GetByRole(*playwright.AriaRoleStatus)).ToContainText("Email changed in this demo"))
	must(t, expect.Locator(page.Locator("#profile-context")).ToContainText(newEmail))

	language := page.Locator(`form[action="/api/v1/dashboard/profile/language"]`)
	selectOption(t, language.Locator(`[name="locale"]`), "pt-BR")
	submitAndExpect(t, page, "/api/v1/dashboard/profile/language", 303, byRole(language, *playwright.AriaRoleButton, "Save language").Click)
	must(t, expect.Locator(page.GetByRole(*playwright.AriaRoleStatus)).ToContainText("Language preference saved"))
	must(t, expect.Locator(page.Locator(`form[action="/api/v1/dashboard/profile/language"] [name="locale"]`)).ToHaveValue("pt-BR"))

	goToOK(t, page, "/profile?tab=security")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Change password"))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Multi-factor authentication"))
	const nextPassword = "E2e-cutover-next-123!"
	password := page.Locator(`form[action="/api/v1/dashboard/profile/password"]`)
	fill(t, password.Locator(`[name="currentPassword"]`), account.Password)
	fill(t, password.Locator(`[name="newPassword"]`), nextPassword)
	fill(t, password.Locator(`[name="confirmPassword"]`), nextPassword)
	submitAndExpect(t, page, "/api/v1/dashboard/profile/password", 303,
		byRole(password, *playwright.AriaRoleButton, "Change password and sign out").Click)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/login" })
	login(t, page, newEmail, nextPassword)
	goToOK(t, page, "/profile?tab=sessions")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Active sessions"))
	sessions := pageByRole(page, *playwright.AriaRoleTable, "Active local dashboard sessions with their current status and expiry.")
	must(t, expect.Locator(sessions).ToContainText("Current session"))
}

func TestCutoverSQLCheckExplainsQueryAndCollectionIndexIsCreated(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("cutover-data"))
	goToOK(t, page, "/databases?create=1")
	database := page.Locator(`form[action="/api/v1/dashboard/databases"]`)
	fill(t, database.Locator(`[name="name"]`), uniqueName("cutover"))
	selectOption(t, database.Locator(`[name="region"]`), "GRU")
	submitAndExpect(t, page, "/api/v1/dashboard/databases", 303, byRole(database, *playwright.AriaRoleButton, "Create database").Click)
	waitForPath(t, page, func(u *url.URL) bool { return strings.HasPrefix(u.Path, "/databases/db_") })
	databasePath := currentPath(t, page)

	goToOK(t, page, databasePath+"/sql?collection=orders")
	query := page.Locator(`form[action="/api/v1/dashboard/data/sql"]`)
	fill(t, query.Locator(`[name="sql"]`), "SELECT * FROM orders WHERE customer_id = $1 ORDER BY created_at DESC LIMIT 10")
	submitAndExpect(t, page, "/api/v1/dashboard/data/sql/check", 200, byRole(query, *playwright.AriaRoleButton, "Check SQL").Click)
	current, err := url.Parse(page.URL())
	must(t, err)
	if current.Query().Has("sql") {
		t.Fatalf("SQL check leaked the statement into the URL: %s", page.URL())
	}
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "SQL check"))
	visible(t, page.GetByText("Server check passed."))
	visible(t, page.GetByText("Statement collection: orders"))
	visible(t, page.GetByText("customer_id", playwright.PageGetByTextOptions{Exact: new(true)}))

	goToOK(t, page, databasePath+"/indexes?collection=orders")
	createIndex := page.Locator(`form[action="/api/v1/dashboard/indexes"]`)
	fill(t, createIndex.Locator(`[name="field"]`), "customer_id")
	submitAndExpect(t, page, "/api/v1/dashboard/indexes", 303, byRole(createIndex, *playwright.AriaRoleButton, "Create index").Click)
	must(t, expect.Locator(page.Locator("#collection-indexes ul")).ToContainText("customer_id"))
}

func TestCutoverSampleIndexRecommendationRecordsDemoBuild(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/databases")
	databaseLink := pageByRole(page, *playwright.AriaRoleLink, "core-primary")
	visible(t, databaseLink)
	databasePath := linkPath(t, page, databaseLink)

	goToOK(t, page, databasePath+"/indexes?view=recommendations&collection=orders")
	recommendation := page.Locator(`form[action="/api/v1/dashboard/indexes/recommendations/apply"]`).First()
	count, err := recommendation.Count()
	must(t, err)
	if count > 0 {
		must(t, expect.Locator(recommendation.Locator("..")).ToContainText("total"))
		submitAndExpect(t, page, "/api/v1/dashboard/indexes/recommendations/apply", 303,
			byRole(recommendation, *playwright.AriaRoleButton, "Apply").Click)
	}
	builds := pageByRole(page, *playwright.AriaRoleHeading, "Demo index builds").Locator("xpath=following-sibling::ul[1]")
	must(t, expect.Locator(builds).ToContainText("total: building"))
	goToOK(t, page, databasePath+"/indexes?collection=orders")
	must(t, expect.Locator(page.Locator("#collection-indexes ul")).ToContainText("total"))
}

func TestCutoverSamplePaymentHistoryFiltersAndPaginatesInvoices(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/usage?tab=billing")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Payment history"))
	rows := pageByRole(page, *playwright.AriaRoleTable, "Invoices for this organization matching the current filters").Locator("tbody tr")
	pages := pageByRole(page, *playwright.AriaRoleNavigation, "Payment history pages")
	must(t, expect.Locator(rows).ToHaveCount(10))
	must(t, expect.Locator(pages).ToContainText("of 14"))
	click(t, byRole(pages, *playwright.AriaRoleLink, "Next"))
	must(t, expect.Locator(rows).ToHaveCount(4))
	must(t, expect.Locator(pages).ToContainText("11–14 of 14"))

	filters := pageByRole(page, *playwright.AriaRoleSearch, "Filter payment history")
	selectOption(t, filters.Locator(`[name="status"]`), "void")
	click(t, byRole(filters, *playwright.AriaRoleButton, "Apply"))
	must(t, expect.Locator(rows).ToHaveCount(1))
	must(t, expect.Locator(rows.First()).ToContainText("void"))
	selectOption(t, filters.Locator(`[name="issued"]`), "3m")
	click(t, byRole(filters, *playwright.AriaRoleButton, "Apply"))
	must(t, expect.Locator(page.Locator(`.ts-empty[role="status"]`)).ToContainText("No invoices match these filters"))
	click(t, byRole(filters, *playwright.AriaRoleLink, "Clear"))
	must(t, expect.Locator(rows).ToHaveCount(10))
}

func TestCutoverFreshAccountChartsShowZeroBaseline(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("cutover-empty-charts"))
	activity := page.Locator("#home-activity svg.ts-chart")
	visible(t, activity)
	must(t, expect.Locator(activity).ToHaveAttribute("aria-label", regexp.MustCompile(`Illustrative zero baseline`)))
	must(t, expect.Locator(page.Locator(".ts-home-activity-total")).ToContainText("Plotted operations"))
	line := requiredAttribute(t, activity.Locator("path.ts-chart-line"), "d", "activity chart must plot a line")
	coordinates := regexp.MustCompile(`[\d.]+,([\d.]+)`).FindAllStringSubmatch(line, -1)
	if len(coordinates) < 2 {
		t.Fatalf("zero baseline path %q must plot more than one point", line)
	}
	for _, coordinate := range coordinates {
		if coordinate[1] != coordinates[0][1] {
			t.Fatalf("zero baseline path %q must be flat", line)
		}
	}

	goToOK(t, page, "/usage")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Workload history"))
	visible(t, page.GetByText("Workload history is unavailable."))
	must(t, expect.Locator(page.Locator(".ts-usage-sample-gauges .ts-usage-ring")).ToHaveCount(0))
	must(t, expect.Locator(page.Locator(".ts-usage-chart-shell svg.ts-chart")).ToHaveCount(0))
}

func TestCutoverSeededChartsRenderLabelledSamplesForSelectedRange(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	activity := page.Locator("#home-activity svg.ts-chart")
	visible(t, activity)
	must(t, expect.Locator(activity).ToHaveAttribute("aria-label", regexp.MustCompile(`Sample organization activity`)))
	sevenDayLine := requiredAttribute(t, activity.Locator("path.ts-chart-line"), "d", "activity chart must plot a line")
	must(t, expect.Locator(page.Locator(".ts-home-activity-total")).ToContainText("Sample operations"))
	goToOK(t, page, "/?range=24h")
	must(t, expect.Locator(page.Locator("#home-activity svg.ts-chart")).ToHaveAttribute("aria-label", regexp.MustCompile(`Last 24 hours`)))
	if requiredAttribute(t, page.Locator("#home-activity path.ts-chart-line"), "d", "24h activity chart must plot a line") == sevenDayLine {
		t.Fatal("24h activity must plot a different line than 7d")
	}

	goToOK(t, page, "/usage?tab=overview&range=7d")
	rings := page.Locator(".ts-usage-sample-gauges .ts-usage-ring .ts-chart-meter[role='img']")
	must(t, expect.Locator(rings).ToHaveCount(2))
	must(t, expect.Locator(rings.Nth(0)).ToHaveAttribute("aria-label", regexp.MustCompile(`81% of illustrative demo capacity`)))
	must(t, expect.Locator(rings.Nth(1)).ToHaveAttribute("aria-label", regexp.MustCompile(`163% of illustrative demo capacity`)))
	visible(t, page.GetByText("Illustrative demo capacity").First())
	workload := page.Locator(".ts-usage-chart-shell svg.ts-chart")
	visible(t, workload)
	for _, series := range []string{"ts-cat-1", "ts-cat-2", "ts-cat-3"} {
		must(t, expect.Locator(workload.Locator("path."+series)).ToHaveCount(1))
	}
	sevenDayHistory := requiredAttribute(t, workload.Locator("path.ts-cat-1"), "d", "workload history must plot a line")
	shareBars := page.Locator(".ts-usage-share svg.ts-svg-sharebar")
	must(t, expect.Locator(shareBars).ToHaveCount(3))
	hasWidth, err := shareBars.Locator("rect.ts-svg-sharebar-fill").EvaluateAll(
		`rects => rects.some((rect) => Number(rect.getAttribute("width")) > 0)`)
	must(t, err)
	if hasWidth != true {
		t.Fatal("at least one usage share bar must have a positive width")
	}
	selectOption(t, page.Locator("#usage-range"), "30d")
	click(t, byRole(pageByRole(page, *playwright.AriaRoleSearch, "Filter usage"), *playwright.AriaRoleButton, "Apply"))
	must(t, expect.Locator(page.Locator("#usage-range")).ToHaveValue("30d"))
	must(t, expect.Locator(page.Locator(".ts-usage-chart-shell svg.ts-chart")).ToHaveAttribute("aria-label", regexp.MustCompile(`Last 30 days`)))
	if requiredAttribute(t, page.Locator(".ts-usage-chart-shell path.ts-cat-1"), "d", "30d workload history must plot a line") == sevenDayHistory {
		t.Fatal("30d workload history must plot a different line than 7d")
	}

	state, err := page.Context().StorageState()
	must(t, err)
	noJSContext, err := browser.NewContext(playwright.BrowserNewContextOptions{
		BaseURL:           &baseURL,
		StorageState:      state.ToOptionalStorageState(),
		JavaScriptEnabled: new(false),
	})
	must(t, err)
	t.Cleanup(func() { _ = noJSContext.Close() })
	noJS, err := noJSContext.NewPage()
	must(t, err)
	goToOK(t, noJS, "/usage?tab=overview&range=7d")
	must(t, expect.Locator(noJS.Locator(".ts-usage-sample-gauges .ts-usage-ring .ts-chart-meter[role='img']")).ToHaveCount(2))
	must(t, expect.Locator(noJS.Locator(".ts-usage-chart-shell path.ts-cat-1")).ToHaveCount(1))
	must(t, expect.Locator(noJS.Locator(".ts-usage-share svg.ts-svg-sharebar rect.ts-svg-sharebar-fill")).ToHaveCount(3))

	goToOK(t, page, "/usage?tab=billing")
	for _, label := range []string{"Next recorded invoice", "Billing period", "Outstanding balance"} {
		visible(t, page.GetByText(label, playwright.PageGetByTextOptions{Exact: new(true)}))
	}
	periodFill := page.Locator(`.ts-usage-billing-kpis .ts-chart-meter[aria-label*="recorded billing period"] rect.ts-svg-sharebar-fill`)
	must(t, expect.Locator(periodFill).ToHaveCount(1))
	var width float64
	if _, err := fmt.Sscan(requiredAttribute(t, periodFill, "width", "billing period fill must have a width"), &width); err != nil || width <= 0 {
		t.Fatalf("billing period fill width must be positive (err %v)", err)
	}
}

func TestCutoverMarioMigrationProgressIsScopedToOrders(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	const migration = "/databases/db_01JC9Q8Y5N7P9R3T5V7X9Z2AC/indexes?view=migration&collection="
	goToOK(t, page, migration+"orders")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Migration progress"))
	visible(t, page.GetByText("Static sample from the Mario in-memory demo", playwright.PageGetByTextOptions{Exact: new(false)}))
	progress := pageByRole(page, *playwright.AriaRoleProgressbar, "Sample index migration progress")
	must(t, expect.Locator(progress).ToHaveAttribute("value", "8640"))
	must(t, expect.Locator(progress).ToHaveAttribute("max", "12480"))
	must(t, expect.Locator(page.Locator(".ts-data-migration")).ToContainText("69%"))
	must(t, expect.Locator(page.Locator(".ts-data-migration")).ToContainText("watermark"))
	goToOK(t, page, migration+"other")
	must(t, expect.Locator(progress).ToHaveCount(0))
	visible(t, page.GetByText("Index migration state is unavailable in the current provider contract."))
}

func TestCutoverAdminEstateActionRecordsAuditEntry(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, admin.Email, admin.Password)
	goToOK(t, page, "/admin/estate")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Regions", true))
	click(t, page.Locator(`a[href="/admin/estate?tab=operations"]`))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Operations", true))
	form := page.Locator(`form[action="/api/v1/admin/estate/action"]`)
	target := uniqueName("db_cutover")
	reason := uniqueName("E2E audit record")
	selectOption(t, form.Locator(`[name="action"]`), "pause")
	fill(t, form.Locator(`[name="targetId"]`), target)
	fill(t, form.Locator(`[name="reason"]`), reason)
	submitAndExpect(t, page, "/api/v1/admin/estate/action", 303, byRole(form, *playwright.AriaRoleButton, "Record action").Click)
	status := page.GetByRole(*playwright.AriaRoleStatus).Filter(playwright.LocatorFilterOptions{HasText: "Action completed and recorded"})
	must(t, expect.Locator(status).ToContainText("No infrastructure action was taken"))
	log := pageByRole(page, *playwright.AriaRoleTable, "Recorded platform administrator estate actions, newest first")
	must(t, expect.Locator(log.Locator("tbody tr").Filter(playwright.LocatorFilterOptions{HasText: target})).ToContainText(reason))
}

func TestCutoverSeededDatabaseDetailTabsRetainControls(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/databases")
	databasePath := linkPath(t, page, pageByRole(page, *playwright.AriaRoleLink, "core-primary"))
	goToOK(t, page, databasePath)
	tabs := pageByRole(page, *playwright.AriaRoleNavigation, "Database sections")
	must(t, expect.Locator(tabs.GetByRole(*playwright.AriaRoleLink)).ToHaveCount(8))
	tab := func(name string) playwright.Locator { return byRole(tabs, *playwright.AriaRoleLink, name) }
	current := func(name string) {
		t.Helper()
		must(t, expect.Locator(tab(name)).ToHaveAttribute("aria-current", "page"))
	}
	current("Overview")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "core-primary"))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Placement and latency"))
	visible(t, page.GetByText("0 ms · demo estimate"))

	click(t, tab("Documents"))
	current("Documents")
	fill(t, page.Locator("#collection"), "orders")
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Open"))
	documents := page.Locator("table.ts-data-documents tbody tr")
	must(t, expect.Locator(documents).ToHaveCount(3))
	click(t, documents.First().Locator(`details.ts-menu > summary[aria-label="Actions for ord_1001"]`))
	click(t, byRole(documents.First(), *playwright.AriaRoleLink, "Edit"))
	editor := pageByRole(page, *playwright.AriaRoleDialog, "ord_1001")
	visible(t, editor)
	must(t, expect.Locator(editor.Locator("#document-json")).ToHaveValue(regexp.MustCompile(`"cus_1001"`)))
	must(t, page.Keyboard().Press("Escape"))
	must(t, expect.Locator(editor).ToBeHidden())

	click(t, tab("SQL"))
	current("SQL")
	must(t, expect.Locator(page.Locator("#sql")).ToHaveValue("SELECT * FROM orders LIMIT 20"))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Saved queries"))

	click(t, tab("Schema"))
	current("Schema")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Collection schema"))

	click(t, tab("Indexes"))
	current("Indexes")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Collection indexes"))

	click(t, tab("Backups"))
	current("Backups")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Restore points"))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Backup policy"))
	visible(t, pageByRole(page, *playwright.AriaRoleButton, "Create backup"))

	click(t, tab("Import and export"))
	current("Import and export")
	must(t, expect.Locator(pageByRole(page, *playwright.AriaRoleButton, "Import JSONL / CSV")).ToBeDisabled())
	must(t, expect.Locator(pageByRole(page, *playwright.AriaRoleButton, "Export lossless JSONL")).ToBeDisabled())

	click(t, tab("Branches"))
	current("Branches")
	branchName := uniqueName("cutover-detail")
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Choose a branch name"))
	branch := page.Locator(`form[action="/api/v1/dashboard/databases/branch"]`).Filter(playwright.LocatorFilterOptions{
		Has: page.Locator(`input[name="name"]`),
	})
	fill(t, branch.Locator(`input[name="name"]`), branchName)
	submitAndExpect(t, page, "/api/v1/dashboard/databases/branch", 303, byRole(branch, *playwright.AriaRoleButton, "Create branch").Click)
	waitForPath(t, page, func(u *url.URL) bool {
		return strings.HasPrefix(u.Path, "/databases/db_") && u.Path != databasePath
	})
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, branchName))
}

func TestCutoverUnknownPageRendersIllustrated404AndAPIReturnsBare404(t *testing.T) {
	t.Parallel()
	context := newContext(t, baseURL)
	page, err := context.NewPage()
	must(t, err)
	const notFound = "We can’t find that page."
	if response := goTo(t, page, "/cutover-page-that-does-not-exist"); response.Status() != 404 {
		t.Fatalf("GET /cutover-page-that-does-not-exist = %d, want 404", response.Status())
	}
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, notFound))
	for _, source := range []string{"/img/404-number.webp", "/img/404-illustration.webp"} {
		image := page.Locator(`img[src^="` + source + `?v="]`)
		visible(t, image)
		eventually(t, source+" to finish loading with a natural width", func() bool {
			loaded, err := image.Evaluate(`image => image.complete && image.naturalWidth > 0`, nil)
			return err == nil && loaded == true
		})
	}

	api, err := context.Request().Get("/api/v1/cutover-route-that-does-not-exist", playwright.APIRequestContextGetOptions{
		Headers: map[string]string{"Accept": "application/json"},
	})
	must(t, err)
	if api.Status() != 404 {
		t.Fatalf("GET /api/v1/cutover-route-that-does-not-exist = %d, want 404", api.Status())
	}
	if contentType := api.Headers()["content-type"]; strings.Contains(contentType, "text/html") {
		t.Fatalf("API 404 content type = %q, must not be HTML", contentType)
	}
	body, err := api.Text()
	must(t, err)
	if strings.Contains(body, notFound) {
		t.Fatal("API 404 must not render the illustrated browser page")
	}
}

// goToOK navigates to path and requires a 200 response.
func goToOK(t *testing.T, page playwright.Page, path string) {
	t.Helper()
	if response := goTo(t, page, path); response.Status() != 200 {
		t.Fatalf("GET %s = %d, want 200", path, response.Status())
	}
}

// submitAndExpect runs submit and requires the POST it triggers on path to
// answer with status.
func submitAndExpect(t *testing.T, page playwright.Page, path string, status int, submit func(...playwright.LocatorClickOptions) error) playwright.Response {
	t.Helper()
	response, err := page.ExpectResponse(func(response playwright.Response) bool {
		parsed, err := url.Parse(response.URL())
		return err == nil && parsed.Path == path && response.Request().Method() == "POST"
	}, func() error { return submit() })
	must(t, err)
	if response.Status() != status {
		t.Fatalf("POST %s returned %d, want %d", path, response.Status(), status)
	}
	return response
}

// wizardStep scopes the new-organization wizard form for one step.
func wizardStep(page playwright.Page, wizard playwright.Locator, step string) playwright.Locator {
	return wizard.Locator(`form[action="` + newOrganizationPath + `"]`).Filter(playwright.LocatorFilterOptions{
		Has: page.Locator(`input[name="step"][value="` + step + `"]`),
	})
}

// linkPath resolves a link's href against the page URL and returns its path.
func linkPath(t *testing.T, page playwright.Page, link playwright.Locator) string {
	t.Helper()
	href := requiredAttribute(t, link, "href", "link must have an href")
	base, err := url.Parse(page.URL())
	must(t, err)
	resolved, err := base.Parse(href)
	must(t, err)
	return resolved.Path
}

// requiredAttribute reads a non-empty attribute or fails with reason.
func requiredAttribute(t *testing.T, locator playwright.Locator, name, reason string) string {
	t.Helper()
	value, err := locator.GetAttribute(name)
	must(t, err)
	if value == "" {
		t.Fatal(reason)
	}
	return value
}
