package e2e

import (
	"slices"
	"strings"
	"testing"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// TestMCPAdminToolsAreListedOnlyForPlatformAdministrators checks that admin_
// tools are hidden from regular accounts and manage the platform for the
// seeded administrator.
func TestMCPAdminToolsAreListedOnlyForPlatformAdministrators(t *testing.T) {
	t.Parallel()
	memberPage := newPage(t)
	registerAndOnboard(t, memberPage, newAccount("mcp-admin-member"))
	member := connectMCP(t, createAccessToken(t, memberPage, journeyPassword, "member assistant"))
	if names := toolNames(t, member); slices.ContainsFunc(names, func(name string) bool { return strings.HasPrefix(name, "admin_") }) {
		t.Fatalf("regular account tools/list exposes administration tools: %v", names)
	}
	if _, err := member.CallTool(t.Context(), &mcp.CallToolParams{Name: "admin_list_users", Arguments: map[string]any{}}); err == nil {
		t.Error("regular account called admin_list_users, want an unknown tool error")
	}

	adminPage := newPage(t)
	login(t, adminPage, admin.Email, admin.Password)
	session := connectMCP(t, createAccessToken(t, adminPage, admin.Password, "admin assistant"))
	names := toolNames(t, session)
	for _, want := range []string{
		"admin_list_users", "admin_list_organizations", "admin_list_projects", "admin_list_databases",
		"admin_get_estate", "admin_get_usage", "admin_list_plans", "admin_update_user",
		"admin_rename_organization", "admin_rename_project", "admin_record_estate_action",
		"admin_save_plan", "admin_set_plan_visibility", "admin_delete_plan",
	} {
		if !slices.Contains(names, want) {
			t.Errorf("administrator tools/list is missing %s", want)
		}
	}

	var users struct {
		Users []struct {
			Email         string
			PlatformAdmin bool
		}
	}
	callTool(t, session, "admin_list_users", map[string]any{"query": admin.Email}, &users)
	if len(users.Users) != 1 || !users.Users[0].PlatformAdmin {
		t.Errorf("admin_list_users(%s) = %+v, want the platform administrator", admin.Email, users.Users)
	}
	var usage struct{ Inventory struct{ Accounts int } }
	callTool(t, session, "admin_get_usage", nil, &usage)
	if usage.Inventory.Accounts < 2 {
		t.Errorf("admin_get_usage accounts = %d, want at least the seeded accounts", usage.Inventory.Accounts)
	}

	if message := callToolError(t, session, "admin_record_estate_action", map[string]any{
		"action": "reboot", "targetId": "4YRZN87C3F8TYQGHKHQH4EMZ8Q", "reason": "not an allowed action",
	}); !strings.Contains(message, "action must be one of") {
		t.Errorf("invalid estate action error = %q, want the allowed actions", message)
	}
	target := uniqueName("0Q1XYVEDXTJYJJFXSYB3EKANCC")
	callTool(t, session, "admin_record_estate_action", map[string]any{"action": "flag", "targetId": target, "reason": "MCP e2e check"}, nil)
	var estate struct {
		Actions []struct{ Action, Target, Reason string }
	}
	callTool(t, session, "admin_get_estate", nil, &estate)
	if !slices.ContainsFunc(estate.Actions, func(a struct{ Action, Target, Reason string }) bool {
		return a.Target == target && a.Action == "flag" && a.Reason == "MCP e2e check"
	}) {
		t.Errorf("admin_get_estate actions = %+v, want the recorded %s action", estate.Actions, target)
	}

	type plan struct {
		ID, Key, Name string
		PriceCents    int64
		Visible       bool
	}
	key := strings.ReplaceAll(uniqueName("mcp-plan"), "-", "_")
	var created plan
	callTool(t, session, "admin_save_plan", map[string]any{
		"key": key, "name": "MCP plan", "priceCents": 1200, "annualPriceCents": 12000,
		"trialDays": 0, "visible": true, "storageMiB": 1024, "monthlyRequests": 1000,
	}, &created)
	if created.ID == "" || created.Key != key || created.PriceCents != 1200 || !created.Visible {
		t.Fatalf("admin_save_plan = %+v, want a visible %s plan", created, key)
	}
	if message := callToolError(t, session, "admin_save_plan", map[string]any{
		"key": key, "name": "Duplicate", "priceCents": 0, "annualPriceCents": 0,
		"trialDays": 0, "visible": false, "storageMiB": 0, "monthlyRequests": 0,
	}); !strings.Contains(message, "already used") {
		t.Errorf("duplicate plan key error = %q, want a conflict", message)
	}
	callTool(t, session, "admin_set_plan_visibility", map[string]any{"planId": created.ID, "visible": false}, nil)
	var plans struct{ Plans []plan }
	callTool(t, session, "admin_list_plans", map[string]any{"query": key}, &plans)
	if len(plans.Plans) != 1 || plans.Plans[0].Visible {
		t.Errorf("admin_list_plans(%s) = %+v, want the hidden plan", key, plans.Plans)
	}
	callTool(t, session, "admin_delete_plan", map[string]any{"planId": created.ID}, nil)
	callTool(t, session, "admin_list_plans", map[string]any{"query": key}, &plans)
	if len(plans.Plans) != 0 {
		t.Errorf("deleted plan is still listed: %+v", plans.Plans)
	}
}
