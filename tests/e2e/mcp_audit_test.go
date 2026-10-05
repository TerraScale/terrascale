package e2e

import (
	"slices"
	"strings"
	"testing"
)

// TestMCPAuditEventsFollowTheAuditPageFilters records a settings change in a
// fresh account and reads it back through list_audit_events.
func TestMCPAuditEventsFollowTheAuditPageFilters(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("mcp-audit"))
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "audit assistant"))
	callTool(t, session, "set_audit_retention", map[string]any{"days": 30}, &struct{ Saved bool }{})
	type event struct{ ID, Action, Target, Severity, Outcome, ActorType string }
	var listed struct {
		Events        []event
		Matching      int
		RetentionDays int
		Actions       []string
		PageCount     int
	}
	callTool(t, session, "list_audit_events", map[string]any{"range": "24h"}, &listed)
	index := slices.IndexFunc(listed.Events, func(e event) bool { return strings.Contains(e.Action, "audit_retention") })
	if index < 0 {
		t.Fatalf("list_audit_events = %+v, want the audit retention change", listed.Events)
	}
	if e := listed.Events[index]; e.Target != "30" || e.Severity != "info" || e.Outcome != "success" || e.ActorType != "user" {
		t.Errorf("audit retention event = %+v, want an info success by a user targeting 30", e)
	}
	if listed.RetentionDays != 30 || listed.Matching < 1 || listed.PageCount != 1 || !slices.Contains(listed.Actions, listed.Events[index].Action) {
		t.Errorf("list_audit_events summary = %+v, want 30 retention days and the action option", listed)
	}

	callTool(t, session, "list_audit_events", map[string]any{"severity": "critical"}, &listed)
	if listed.Matching != 0 || len(listed.Events) != 0 {
		t.Errorf("critical audit events = %+v, want none for a low-risk change", listed.Events)
	}
	if message := callToolError(t, session, "list_audit_events", map[string]any{"pageSize": 500}); !strings.Contains(message, "pageSize") {
		t.Errorf("oversized page error = %q, want the pageSize limit", message)
	}
}
