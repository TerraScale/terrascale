package e2e

import (
	"strings"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// TestMCPOverviewMatchesHomeAndConnectTabDescribesTheServer compares the
// overview tool with the home page and checks the MCP connection guide.
func TestMCPOverviewMatchesHomeAndConnectTabDescribesTheServer(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("mcp-home"))
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "overview"))

	callTool(t, session, "create_database", map[string]any{"name": uniqueName("home"), "region": "GRU"}, nil)
	var overview struct {
		ProjectName         string
		DatabaseCount       int
		OrganizationMembers int
		Activity            struct {
			Range  string
			Sample bool
			Points []int64
		}
		GettingStarted []struct {
			Title string
			Done  bool
		}
	}
	callTool(t, session, "get_overview", map[string]any{"range": "24h"}, &overview)
	if overview.ProjectName != "Personal" || overview.DatabaseCount != 1 || overview.OrganizationMembers != 1 {
		t.Errorf("get_overview = %+v, want the Personal project with one database and member", overview)
	}
	if overview.Activity.Range != "24h" || len(overview.Activity.Points) != 24 || overview.Activity.Sample {
		t.Errorf("get_overview activity = %+v, want a 24-point zero baseline", overview.Activity)
	}
	if len(overview.GettingStarted) != 2 || !overview.GettingStarted[0].Done || overview.GettingStarted[1].Done {
		t.Errorf("get_overview getting started = %+v, want only the database task done", overview.GettingStarted)
	}
	if message := callToolError(t, session, "get_overview", map[string]any{"projectId": "prj_missing"}); !strings.Contains(message, "not found") {
		t.Errorf("unknown project overview error = %q", message)
	}

	goTo(t, page, "/?tab=connect")
	card := page.Locator("#mcp-config")
	must(t, expect.Locator(card.Locator("pre")).ToContainText(baseURL+"/mcp"))
	must(t, expect.Locator(card.Locator("pre")).ToContainText("Authorization"))
	link := byRole(card, *playwright.AriaRoleLink, "Open access tokens")
	if path := linkPath(t, page, link); path != "/profile" {
		t.Errorf("access token link path = %q, want /profile", path)
	}
	must(t, expect.Locator(page.Locator(".ts-home-connect-item").Filter(playwright.LocatorFilterOptions{HasText: "AI assistants"})).ToContainText("Available"))
	screenshot(t, page, "home-connect-mcp")
}
