package e2e

import (
	"slices"
	"strings"
	"testing"
)

// TestMCPUsageReportsAndSetsSpendingControls reads usage and changes spend
// caps through the MCP tools.
func TestMCPUsageReportsAndSetsSpendingControls(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("mcp-usage"))
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "usage assistant"))

	var database struct{ ID string }
	callTool(t, session, "create_database", map[string]any{"name": uniqueName("usage"), "region": "GRU"}, &database)

	type report struct {
		ProjectID                string
		PlanName                 string
		SpendCapCents            int64
		PaidExcessEnabled        bool
		ProjectEffectiveCapCents int64
		CanManage                bool
		Projects                 []struct {
			ID            string
			SpendCapCents int64
		}
		Databases []struct {
			ID                string
			EffectiveCapCents int64
		}
		Allowances []struct{ Name string }
	}
	var usage report
	callTool(t, session, "get_usage", map[string]any{"databaseId": database.ID}, &usage)
	if usage.PlanName == "" || !usage.CanManage || len(usage.Allowances) != 3 || len(usage.Databases) != 1 || usage.Databases[0].ID != database.ID {
		t.Fatalf("get_usage = %+v, want the plan, manage access, reads, writes and storage allowances, and only the filtered database", usage)
	}

	var capped report
	callTool(t, session, "set_spending_controls", map[string]any{
		"organizationCapCents": 5000, "paidExcess": true,
		"projectCaps":  []map[string]any{{"id": usage.ProjectID, "capCents": 2500}},
		"databaseCaps": []map[string]any{{"id": database.ID, "capCents": 1000}},
	}, &capped)
	if capped.SpendCapCents != 5000 || !capped.PaidExcessEnabled || capped.ProjectEffectiveCapCents != 2500 {
		t.Errorf("set_spending_controls = %+v, want a 5000 organization cap, paid excess, and 2500 effective project cap", capped)
	}
	index := slices.IndexFunc(capped.Databases, func(d struct {
		ID                string
		EffectiveCapCents int64
	}) bool {
		return d.ID == database.ID
	})
	if index < 0 || capped.Databases[index].EffectiveCapCents != 1000 {
		t.Errorf("database caps after set_spending_controls = %+v, want %s capped at 1000", capped.Databases, database.ID)
	}

	if message := callToolError(t, session, "set_spending_controls", map[string]any{"organizationCapCents": -1}); !strings.Contains(message, "zero or positive") {
		t.Errorf("negative cap error = %q, want the cap rule", message)
	}
	if message := callToolError(t, session, "set_spending_controls", map[string]any{"projectCaps": []map[string]any{{"id": usage.ProjectID, "capCents": 100}, {"id": usage.ProjectID, "capCents": 200}}}); !strings.Contains(message, "duplicate project cap") {
		t.Errorf("duplicate project cap error = %q, want duplicate rejection", message)
	}
	if message := callToolError(t, session, "set_spending_controls", map[string]any{"databaseCaps": []map[string]any{{"id": "", "capCents": 100}}}); !strings.Contains(message, "database id is required") {
		t.Errorf("empty database cap ID error = %q, want ID validation", message)
	}
	if message := callToolError(t, session, "get_usage", map[string]any{"databaseId": "7CWZH5HEGFV2Z8DWE3144TJQSS"}); !strings.Contains(message, "not found") {
		t.Errorf("unknown database filter error = %q, want not found", message)
	}
}
