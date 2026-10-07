package e2e

import (
	"fmt"
	"os"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

func TestOnboardingRegionTooltipsShowPing(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOpenPaymentStep(t, page, newAccount("region-ping"))
	click(t, byRole(onboardingStep(t, page, "payment-method"), *playwright.AriaRoleButton, "Continue on Free"))
	project := onboardingStep(t, page, "project")
	regionMap := project.Locator(`.ts-region-map[data-select-id="region"]`)

	for _, viewport := range []struct{ width, height int }{{1280, 800}, {320, 700}} {
		must(t, page.SetViewportSize(viewport.width, viewport.height))
		for _, tc := range []struct{ code, ping string }{
			{"SFO", "Ping: 28 ms (demo)"},
			{"GRU", "Ping: 118 ms (demo)"},
			{"FRA", "Ping: 74 ms (demo)"},
		} {
			pin := regionMap.Locator(`button[data-region="` + tc.code + `"]`)
			label := pin.Locator(".ts-region-node-label")
			must(t, pin.Hover())
			must(t, expect.Locator(label).ToHaveCSS("opacity", "1"))
			must(t, expect.Locator(label.Locator(".ts-region-node-ping")).ToHaveText(tc.ping))
			must(t, expect.Locator(pin).ToHaveAttribute("aria-description", tc.ping))
			// Hovering previews the ping without changing the canonical region.
			must(t, expect.Locator(project.Locator("#region")).ToHaveValue("GRU"))
			mapBounds, err := regionMap.BoundingBox()
			must(t, err)
			labelBounds, err := label.BoundingBox()
			must(t, err)
			if labelBounds.X < mapBounds.X || labelBounds.X+labelBounds.Width > mapBounds.X+mapBounds.Width {
				t.Fatalf("%s ping tooltip %+v exceeds map %+v at %dpx", tc.code, labelBounds, mapBounds, viewport.width)
			}
			if os.Getenv("DASHBOARD_E2E_SCREENSHOTS") != "" {
				// Keyboard focus keeps this tooltip visible as the capture helper resizes.
				must(t, pin.Focus())
				must(t, pin.Press("ArrowRight"))
				screenshot(t, page, fmt.Sprintf("onboarding-ping-%s-%d", tc.code, viewport.width))
				if tc.code == "GRU" {
					// Reproduce the original name-only tooltip for the review's comparison.
					_, err := page.Evaluate(`() => { const s = document.styleSheets[0]; s.insertRule('.ts-region-node-ping { display: none !important; }', s.cssRules.length); }`)
					must(t, err)
					screenshot(t, page, fmt.Sprintf("onboarding-before-GRU-%d", viewport.width))
					_, err = page.Evaluate(`() => { const s = document.styleSheets[0]; s.deleteRule(s.cssRules.length - 1); }`)
					must(t, err)
				}
				_, err := pin.Evaluate(`el => el.blur()`, nil)
				must(t, err)
			}
		}
	}

	must(t, page.SetViewportSize(1280, 800))
	must(t, project.Locator("#projectName").Hover())
	frankfurt := regionMap.Locator(`button[data-region="FRA"]`)
	must(t, regionMap.Locator(`button[data-region="GRU"]`).Focus())
	must(t, page.Keyboard().Press("Tab"))
	must(t, expect.Locator(frankfurt).ToBeFocused())
	must(t, expect.Locator(frankfurt.Locator(".ts-region-node-label")).ToHaveCSS("opacity", "1"))
	must(t, expect.Locator(frankfurt.Locator(".ts-region-node-ping")).ToHaveText("Ping: 74 ms (demo)"))
	must(t, frankfurt.Press("Space"))
	must(t, expect.Locator(project.Locator("#region")).ToHaveValue("FRA"))
	expectAccessible(t, page, "onboarding region ping tooltips", "")
}
