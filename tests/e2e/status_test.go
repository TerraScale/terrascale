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
	statusPage := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/v1/summary.json" {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{
			"status": {"indicator": "major", "description": "Partial System Outage"},
			"incidents": [{"id": "e2e", "name": "Elevated database API errors", "status": "investigating", "impact": "major",
				"shortlink": "https://status.terrascale.tech/incidents/e2e",
				"incident_updates": [{"body": "` + update + `"}]}],
			"scheduled_maintenances": [{"id": "e2e-maintenance", "name": "Storage engine upgrade", "status": "scheduled",
				"scheduled_for": "` + starts.Format(time.RFC3339) + `",
				"scheduled_until": "` + starts.Add(time.Hour).Format(time.RFC3339) + `",
				"shortlink": "https://status.terrascale.tech/incidents/e2e-maintenance"}]
		}`))
	}))
	t.Cleanup(statusPage.Close)
	application, err := app.New(config.Config{Demo: true, StatusURL: statusPage.URL + "/api/v1/summary.json"})
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

	// Both tabs use the same browser login; another context has its own login.
	secondTab := watchedPage(t, page.Context())
	goTo(t, secondTab, "/usage")
	otherLogin := watchedPage(t, newContext(t, dashboard.URL))
	login(t, otherLogin, sampleAccount.Email, sampleAccount.Password)
	goTo(t, otherLogin, "/databases")
	scope, err := banner.GetAttribute("data-notice-scope")
	must(t, err)
	cookies, err := page.Context().Cookies()
	must(t, err)
	for _, cookie := range cookies {
		if cookie.Name == sessionCookie {
			content, err := page.Content()
			must(t, err)
			if strings.Contains(content, cookie.Value) {
				t.Fatal("banner exposed the session credential in the document")
			}
		}
	}

	closeIncident := pageByRole(page, *playwright.AriaRoleButton, "Dismiss alert: Elevated database API errors")
	must(t, closeIncident.Focus())
	must(t, closeIncident.Press("Enter"))
	must(t, expect.Locator(incident).ToBeHidden())
	must(t, expect.Locator(maintenance).ToBeVisible())
	must(t, expect.Locator(secondTab.Locator(`[data-notice-id="incident:e2e"]`)).ToBeHidden())
	must(t, expect.Locator(otherLogin.Locator(`[data-notice-id="incident:e2e"]`)).ToBeVisible())
	must(t, expect.Locator(pageByRole(page, *playwright.AriaRoleButton, "Dismiss alert: Storage engine upgrade")).ToBeFocused())
	screenshot(t, page, "status-banner-incident-dismissed")

	goTo(t, page, "/usage")
	must(t, expect.Locator(page.Locator(`[data-notice-id="incident:e2e"]`)).ToBeHidden())
	goTo(t, page, "/databases")
	_, err = page.Reload()
	must(t, err)
	must(t, expect.Locator(incident).ToBeHidden())
	must(t, expect.Locator(banner).ToHaveAttribute("data-notice-scope", scope))
	click(t, pageByRole(secondTab, *playwright.AriaRoleButton, "Dismiss alert: Storage engine upgrade"))
	must(t, expect.Locator(banner).ToBeHidden())
	must(t, expect.Locator(secondTab.Locator(".ts-status-banner")).ToBeHidden())
	must(t, expect.Locator(secondTab.Locator("#ts-main")).ToBeFocused())
	screenshot(t, page, "status-banner-all-dismissed")

	logout(t, page)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goTo(t, page, "/databases")
	must(t, expect.Locator(incident).ToBeVisible())
	must(t, expect.Locator(maintenance).ToBeVisible())
	must(t, expect.Locator(banner).Not().ToHaveAttribute("data-notice-scope", scope))
	goTo(t, secondTab, "/usage")
	must(t, expect.Locator(secondTab.Locator(`[data-notice-id="incident:e2e"]`)).ToBeVisible())

	// Storage restrictions must not break the page or the close control.
	_, err = page.Evaluate(`() => Object.defineProperty(window, 'localStorage', {get() { throw new DOMException('Blocked', 'SecurityError'); }})`)
	must(t, err)
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Dismiss alert: Elevated database API errors"))
	must(t, expect.Locator(incident).ToBeHidden())
	must(t, expect.Locator(maintenance).ToBeVisible())

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
