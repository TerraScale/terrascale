package e2e

import (
	"net/url"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

func TestSpendingControlsSaveAcrossProjectsAndDatabases(t *testing.T) {
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("spending-controls"))
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "spending controls"))
	var context mcpContext
	callTool(t, session, "get_context", nil, &context)
	first := context.Selection.ProjectID
	var database struct{ ID string }
	callTool(t, session, "create_database", map[string]any{"name": uniqueName("budget"), "region": "GRU"}, &database)
	var second struct{ ID string }
	callTool(t, session, "create_project", map[string]any{"name": "Analytics", "region": "SFO"}, &second)
	goToOK(t, page, "/usage?tab=settings")
	form := page.Locator("#spending-controls")
	must(t, form.Locator(`[name="spendCap"]`).Fill("50.25"))
	must(t, form.Locator(`[name="paidExcess"]`).Check())
	must(t, form.Locator(`[name="projCap_`+first+`"]`).Fill("25.50"))
	must(t, form.Locator(`[name="projRead_`+first+`"]`).Fill("1000"))
	must(t, form.Locator(`[name="projWrite_`+first+`"]`).Fill("2000"))
	must(t, form.Locator(`[name="projReplication_`+first+`"]`).Fill("3000"))
	must(t, form.Locator(`[name="dbCap_`+database.ID+`"]`).Fill("10.75"))
	must(t, form.Locator(`[name="dbRead_`+database.ID+`"]`).Fill("100"))
	must(t, form.Locator(`[name="dbWrite_`+database.ID+`"]`).Fill("200"))
	must(t, form.Locator(`[name="dbReplication_`+database.ID+`"]`).Fill("300"))
	selector := form.Locator("#spending-project")
	must(t, form.Locator(`[name="projRead_`+first+`"]`).Fill("1000.5"))
	_, err := selector.SelectOption(playwright.SelectOptionValues{Values: &[]string{second.ID}})
	must(t, err)
	visible(t, form.GetByText("No databases yet", playwright.LocatorGetByTextOptions{Exact: new(true)}))
	must(t, form.Locator(`[name="projCap_`+second.ID+`"]`).Fill("12.00"))
	// Native validation reveals a hidden project's invalid field without saving.
	click(t, byRole(form, *playwright.AriaRoleButton, "Save spending controls"))
	must(t, expect.Locator(selector).ToHaveValue(first))
	must(t, form.Locator(`[name="projRead_`+first+`"]`).Fill("1000"))
	_, err = selector.SelectOption(playwright.SelectOptionValues{Values: &[]string{first}})
	must(t, err)
	must(t, expect.Locator(form.Locator(`[name="projRead_`+first+`"]`)).ToHaveValue("1000"))
	_, err = selector.SelectOption(playwright.SelectOptionValues{Values: &[]string{second.ID}})
	must(t, err)
	click(t, byRole(form, *playwright.AriaRoleButton, "Save spending controls"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Query().Get("saved") == "ok" })
	savedURL, err := url.Parse(page.URL())
	must(t, err)
	if savedURL.Query().Get("project") != second.ID {
		t.Fatalf("save did not retain the selected project: %s", page.URL())
	}
	visible(t, page.GetByText("Spending controls saved.", playwright.PageGetByTextOptions{Exact: new(true)}))
	expectAccessible(t, page, "Saved spending controls", "")
	screenshot(t, page, "ts765-spending-saved")
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Dismiss save confirmation", true))
	must(t, expect.Locator(page.Locator(".ts-spending-notice")).ToBeHidden())
	type limits struct{ ReadLimit, WriteLimit, ReplicationLimit int64 }
	type report struct {
		SpendCapCents     int64
		PaidExcessEnabled bool
		Projects          []struct {
			ID              string
			SpendCapCents   int64
			OperationLimits limits
		}
		Databases []struct {
			SpendCapCents   int64
			OperationLimits limits
		}
	}
	var saved report
	callTool(t, session, "get_usage", map[string]any{"projectId": first}, &saved)
	if saved.SpendCapCents != 5025 || !saved.PaidExcessEnabled || len(saved.Databases) != 1 || saved.Databases[0].SpendCapCents != 1075 || saved.Databases[0].OperationLimits != (limits{100, 200, 300}) {
		t.Fatalf("browser settings were not reflected in the backend: %+v", saved)
	}
	for _, project := range saved.Projects {
		if project.ID == first && (project.SpendCapCents != 2550 || project.OperationLimits != (limits{1000, 2000, 3000})) {
			t.Fatalf("project operations were not saved: %+v", project)
		}
		if project.ID == second.ID && project.SpendCapCents != 1200 {
			t.Fatalf("second project draft was lost: %+v", project)
		}
	}
	goToOK(t, page, "/usage?tab=settings&project="+first)
	must(t, expect.Locator(form.Locator(`[name="dbWrite_`+database.ID+`"]`)).ToHaveValue("200"))
	must(t, form.Locator(`[name="spendCap"]`).Fill(""))
	must(t, form.Locator(`[name="paidExcess"]`).Uncheck())
	must(t, form.Locator(`[name="projRead_`+first+`"]`).Fill(""))
	must(t, form.Locator(`[name="dbCap_`+database.ID+`"]`).Fill("0"))
	click(t, byRole(form, *playwright.AriaRoleButton, "Save spending controls"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Query().Get("saved") == "ok" })
	callTool(t, session, "get_usage", map[string]any{"projectId": first}, &saved)
	if saved.SpendCapCents != 0 || saved.PaidExcessEnabled || saved.Databases[0].SpendCapCents != 0 {
		t.Fatalf("clearing caps or disabling paid excess was not saved: %+v", saved)
	}
}

func TestSpendingControlsResponsiveAndAccessible(t *testing.T) {
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/usage?tab=settings")
	for _, width := range []int{360, 390, 768, 1024, 1440} {
		must(t, page.SetViewportSize(width, 900))
		result, err := page.Evaluate(`() => ({overflow: document.documentElement.scrollWidth > innerWidth + 1,
			inputs: [...document.querySelectorAll('#spending-controls .ts-input')].filter(el => el.getClientRects().length)
				.every(el => el.getBoundingClientRect().right <= el.closest('fieldset').getBoundingClientRect().right + 1)})`)
		must(t, err)
		layout := result.(map[string]any)
		if layout["overflow"] == true || layout["inputs"] != true {
			t.Fatalf("spending controls overflow at %dpx: %v", width, layout)
		}
	}
	expectAccessible(t, page, "Spending controls", "")
	screenshot(t, page, "ts765-spending-controls")
}
