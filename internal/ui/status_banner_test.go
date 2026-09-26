package ui

import (
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestStatusBannerRendersNothingWhenAllIsClear(t *testing.T) {
	var out strings.Builder
	if err := StatusBanner(nil).Render(t.Context(), &out); err != nil {
		t.Fatal(err)
	}
	if out.Len() != 0 {
		t.Fatalf("banner without notices rendered %q", out.String())
	}
}

func TestStatusBannerRendersEachNoticeByImpact(t *testing.T) {
	var out strings.Builder
	err := StatusBanner([]core.StatusNotice{
		{Impact: "major", Label: "Major incident", Title: "Database API errors",
			Message: "We are investigating <elevated> error rates.", URL: "https://status.terrascale.tech/incidents/2"},
		{Impact: "maintenance", Label: "Scheduled maintenance", Title: "Storage migration",
			URL: "https://status.terrascale.tech/incidents/4"},
	}).Render(t.Context(), &out)
	if err != nil {
		t.Fatal(err)
	}
	html := out.String()
	for _, want := range []string{
		`<div class="ts-status-banner" role="status" aria-label="TerraScale status">`,
		`<div class="ts-status-notice" data-impact="major">`,
		`<span class="ts-status-notice-label">Major incident<span class="ts-sr-only">:</span></span>`,
		`<span class="ts-status-notice-title">Database API errors</span>`,
		`<span class="ts-status-notice-message">We are investigating &lt;elevated&gt; error rates.</span>`,
		`href="https://status.terrascale.tech/incidents/2"`,
		`<div class="ts-status-notice" data-impact="maintenance">`,
		`href="https://status.terrascale.tech/incidents/4"`,
	} {
		if !strings.Contains(html, want) {
			t.Errorf("banner is missing %s in:\n%s", want, html)
		}
	}
	if got := strings.Count(html, "ts-status-notice-message"); got != 1 {
		t.Errorf("rendered %d messages, want only the notice that has one", got)
	}
	if strings.Contains(html, "style=") {
		t.Error("banner uses an inline style, which the CSP blocks")
	}
}
