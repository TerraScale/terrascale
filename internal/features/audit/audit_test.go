package audit

import (
	"context"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

func TestAuditFilteringHonorsRangeRetentionSeverityAndSearch(t *testing.T) {
	now := time.Date(2026, time.September, 24, 12, 0, 0, 0, time.UTC)
	events := []core.AuditEvent{
		{ID: "new", Actor: "ana@example.test", Action: "database.deleted", Target: "production", Risk: "high", OccurredAt: now.Add(-time.Hour)},
		{ID: "week", Actor: "jules@example.test", Action: "api_key.created", Target: "demo", Risk: "low", OccurredAt: now.Add(-8 * 24 * time.Hour)},
		{ID: "retention", Actor: "system", Action: "backup.completed", Target: "old", Risk: "medium", OccurredAt: now.Add(-31 * 24 * time.Hour)},
	}
	data, filtered := buildPage(events, 30, "ANA", "all", "critical", "90d", "1", now)
	if len(filtered) != 1 || filtered[0].ID != "new" {
		t.Fatalf("filtered events = %+v, want only recent matching critical event", filtered)
	}
	if data.Critical != 1 || data.Warn != 0 || data.Info != 0 || data.Rows[0].ActionLabel != "Database deleted" {
		t.Fatalf("page severity/action labels = %+v", data)
	}

	data, filtered = buildPage(events, 7, "", "all", "all", "90d", "0", now)
	if len(filtered) != 1 || filtered[0].ID != "new" || data.Page != 1 {
		t.Fatalf("retention/page clamp result = %+v, %+v", data, filtered)
	}
}

func TestAuditViewEscapesUntrustedContent(t *testing.T) {
	attack := `<script>alert("x")</script>`
	data := pageData{
		Rows: []row{{
			Event:       core.AuditEvent{Actor: attack, Action: attack, Target: attack, Source: attack, OccurredAt: time.Date(2026, time.September, 24, 12, 0, 0, 0, time.UTC)},
			ActionLabel: attack, ActorType: "user", Severity: "info", Outcome: "success",
		}},
		Search: attack, Action: "all", Severity: "all", Range: "7d", ActionOptions: []string{"all", attack},
		Filtered: 1, Page: 1, PageCount: 1, Retention: 90,
	}
	response := httptest.NewRecorder()
	if err := auditView(data).Render(context.Background(), response); err != nil {
		t.Fatalf("render audit view: %v", err)
	}
	body := response.Body.String()
	if strings.Contains(body, attack) || !strings.Contains(body, "&lt;script&gt;") {
		t.Fatalf("audit content was not safely escaped: %s", body)
	}
}

func TestAuditExportActionsUseRocketEnhancedNativeMenu(t *testing.T) {
	data := pageData{Action: "api_key.created", Severity: "critical", Range: "30d", Search: "user@example.test"}
	response := httptest.NewRecorder()
	if err := auditView(data).Render(context.Background(), response); err != nil {
		t.Fatalf("render audit view: %v", err)
	}
	body := response.Body.String()
	if !strings.Contains(body, `<ts-menu><details class="ts-menu ts-menu-end">`) {
		t.Fatalf("audit export is not wrapped in the Rocket-enhanced native menu: %s", body)
	}
	for _, expected := range []string{
		`<main class="ts-page ts-audit-page">`, `<header class="ts-toolbar">`,
		`class="ts-filters ts-audit-filters"`, `class="ts-filter-field ts-audit-search-field"`,
		`class="ts-input"`, `class="ts-select"`, `class="ts-btn ts-btn-primary"`,
		`aria-label="Export audit events"`,
		`href="/audit/export?format=csv&amp;range=30d&amp;severity=critical&amp;action=api_key.created&amp;search=user%40example.test"`,
		`href="/audit/export?format=json&amp;range=30d&amp;severity=critical&amp;action=api_key.created&amp;search=user%40example.test"`,
		"Export CSV", "Export JSON",
	} {
		if !strings.Contains(body, expected) {
			t.Errorf("audit export menu is missing %q", expected)
		}
	}
}
