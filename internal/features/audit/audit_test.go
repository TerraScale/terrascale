package audit

import (
	"html/template"
	"io/fs"
	"testing"
	"testing/fstest"
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

func TestAuditTemplateParses(t *testing.T) {
	data, err := fs.ReadFile(featureTemplates, "templates/audit.html")
	if err != nil {
		t.Fatal(err)
	}
	files := fstest.MapFS{"audit.html": {Data: data}}
	if _, err := template.ParseFS(files, "*.html"); err != nil {
		t.Fatalf("parse feature template: %v", err)
	}
}
