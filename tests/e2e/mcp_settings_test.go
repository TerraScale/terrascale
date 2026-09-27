package e2e

import (
	"strings"
	"testing"
)

// TestMCPSettingsChangeAuditRetentionAndAuthentication reads and changes a
// project's settings through the MCP tools and keeps the default project.
func TestMCPSettingsChangeAuditRetentionAndAuthentication(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("mcp-settings"))
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "settings assistant"))

	type settings struct {
		ProjectName           string
		AuthenticationEnabled bool
		AuditRetentionDays    int
		CanManage             bool
		ProjectCanBeDeleted   bool
	}
	var before settings
	callTool(t, session, "get_project_settings", nil, &before)
	if !before.CanManage || before.ProjectCanBeDeleted || before.AuditRetentionDays != 90 || before.ProjectName == "" {
		t.Fatalf("get_project_settings = %+v, want a manageable, undeletable default project with 90-day retention", before)
	}

	callTool(t, session, "set_audit_retention", map[string]any{"days": 30}, nil)
	callTool(t, session, "set_project_authentication", map[string]any{"enabled": !before.AuthenticationEnabled}, nil)
	var after settings
	callTool(t, session, "get_project_settings", nil, &after)
	if after.AuditRetentionDays != 30 || after.AuthenticationEnabled == before.AuthenticationEnabled {
		t.Errorf("get_project_settings after changes = %+v, want 30-day retention and toggled authentication", after)
	}

	if message := callToolError(t, session, "set_audit_retention", map[string]any{"days": 120}); !strings.Contains(message, "1 to 90") {
		t.Errorf("set_audit_retention(120) error = %q, want the retention range", message)
	}
	if message := callToolError(t, session, "delete_project", map[string]any{"confirmName": "wrong"}); !strings.Contains(message, "confirm") {
		t.Errorf("unconfirmed delete_project error = %q, want a confirmation error", message)
	}
	if message := callToolError(t, session, "delete_project", map[string]any{"confirmName": before.ProjectName}); !strings.Contains(message, "default or last project") {
		t.Errorf("deleting the default project error = %q, want the protection rule", message)
	}
}
