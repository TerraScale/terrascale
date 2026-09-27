package e2e

import (
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/mxschmitt/playwright-go"

	"terrascale.tech/dashboard/internal/app"
	"terrascale.tech/dashboard/internal/platform/config"
)

func TestHealthProbesReportTheBuildVersion(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	for _, path := range []string{"/health/ready", "/health/live"} {
		var probe struct {
			Status  string `json:"status"`
			Version string `json:"version"`
		}
		responseJSON(t, apiGet(t, page.Request(), path, ""), http.StatusOK, &probe)
		if probe.Status != "ok" || probe.Version == "" {
			t.Fatalf("GET %s = %+v, want status ok and a build version", path, probe)
		}
	}
}

func TestShellAnnouncesStatusPageIncidentsAndMaintenance(t *testing.T) {
	if testing.Short() {
		t.Skip("starts a second in-process dashboard with a status page fixture")
	}
	t.Parallel()
	starts := time.Now().UTC().Add(3 * time.Hour).Truncate(time.Hour)
	// A long, two-paragraph update: the banner quotes the start of the first.
	update := strings.Repeat("We are investigating elevated error rates in gru. ", 12) + `\n\nRoot cause: a saturated connection pool.`
	statusPage := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{
			"status": {"indicator": "major", "description": "Partial System Outage"},
			"incidents": [{"name": "Elevated database API errors", "status": "investigating", "impact": "major",
				"shortlink": "https://status.terrascale.tech/incidents/e2e",
				"incident_updates": [{"body": "` + update + `"}]}],
			"scheduled_maintenances": [{"name": "Storage engine upgrade", "status": "scheduled",
				"scheduled_for": "` + starts.Format(time.RFC3339) + `",
				"scheduled_until": "` + starts.Add(time.Hour).Format(time.RFC3339) + `",
				"shortlink": "https://status.terrascale.tech/incidents/e2e-maintenance"}]
		}`))
	}))
	t.Cleanup(statusPage.Close)
	application, err := app.New(config.Config{Demo: true, StatusURL: statusPage.URL + "/api/v2/summary.json"})
	must(t, err)
	dashboard := httptest.NewServer(application.Handler)
	t.Cleanup(dashboard.Close)

	page := watchedPage(t, newContext(t, dashboard.URL))
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goTo(t, page, "/databases")
	banner := pageByRole(page, *playwright.AriaRoleStatus, "TerraScale status")
	must(t, expect.Locator(banner).ToBeVisible())
	incident := banner.Locator(`[data-impact="major"]`)
	must(t, expect.Locator(incident).ToContainText("Major incident"))
	must(t, expect.Locator(incident).ToContainText("Elevated database API errors"))
	message := incident.Locator(".ts-status-notice-message")
	must(t, expect.Locator(message).ToContainText("We are investigating elevated error rates in gru."))
	must(t, expect.Locator(message).Not().ToContainText("Root cause"))
	must(t, expect.Locator(byRole(incident, *playwright.AriaRoleLink, "View status of Elevated database API errors")).
		ToHaveAttribute("href", "https://status.terrascale.tech/incidents/e2e"))
	maintenance := banner.Locator(`[data-impact="maintenance"]`)
	must(t, expect.Locator(maintenance).ToContainText("Scheduled maintenance"))
	must(t, expect.Locator(maintenance).ToContainText("Storage engine upgrade"))
	screenshot(t, page, "status-banner")
	must(t, page.SetViewportSize(390, 844))
	box, err := banner.BoundingBox()
	must(t, err)
	if box.Height > 280 {
		t.Fatalf("banner is %.0fpx tall on a 390px wide screen, want at most 280px", box.Height)
	}

	// The in-process shared dashboard reads no status page, so its shell
	// shows no banner.
	if os.Getenv("DASHBOARD_E2E_BASE_URL") == "" {
		allClear := newPage(t)
		login(t, allClear, sampleAccount.Email, sampleAccount.Password)
		goTo(t, allClear, "/databases")
		must(t, expect.Locator(allClear.Locator(".ts-status-banner")).ToHaveCount(0))
		screenshot(t, allClear, "status-banner-clear")
	}
}
