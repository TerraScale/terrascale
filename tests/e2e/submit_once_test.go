package e2e

import (
	"net/http"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/mxschmitt/playwright-go"
)

// A double click on the sign-up button used to send two registrations: the
// first created the account, the browser dropped its session cookie when the
// second navigation started, and the second reported the email as taken.
func TestSignUpDoubleClickSendsOneRegistration(t *testing.T) {
	t.Parallel()
	account := newAccount("double-click")
	page := newPage(t)

	goTo(t, page, "/onboarding")
	form := page.Locator("form[data-submit-once]")
	fill(t, form.Locator(`[name="displayName"]`), account.DisplayName)
	fill(t, form.Locator(`[name="email"]`), account.Email)
	fill(t, form.Locator(`[name="password"]`), account.Password)
	fill(t, form.Locator(`[name="confirmPassword"]`), account.Password)
	must(t, form.Locator(`[name="terms"]`).Check())
	action, err := form.GetAttribute("action")
	must(t, err)

	// Hold each registration until both clicks have landed on this page.
	var registrations atomic.Int32
	release := make(chan struct{})
	releaseOnce := sync.OnceFunc(func() { close(release) })
	t.Cleanup(releaseOnce)
	must(t, page.Route("**"+action, func(route playwright.Route) {
		if route.Request().Method() == http.MethodPost {
			registrations.Add(1)
			select {
			case <-release:
			case <-time.After(10 * time.Second):
			}
		}
		_ = route.Continue()
	}))

	submit := form.Locator(`[type="submit"]`)
	busy, err := submit.Evaluate(`button => {
		document.body.classList.add('ts-nav-pending')
		const cursor = getComputedStyle(button).cursor
		document.body.classList.remove('ts-nav-pending')
		return cursor
	}`, nil)
	must(t, err)
	if busy != "progress" {
		t.Fatalf("submit button cursor while the page loads = %v, want progress", busy)
	}

	// Playwright waits for a pending navigation before evaluating anything on
	// the page, so only mouse input and the route counter are used while the
	// first registration is held.
	must(t, submit.ScrollIntoViewIfNeeded())
	box, err := submit.BoundingBox()
	must(t, err)
	x, y := box.X+box.Width/2, box.Y+box.Height/2
	must(t, page.Mouse().Click(x, y))
	eventually(t, "the first registration to be held", func() bool { return registrations.Load() == 1 })
	must(t, page.Mouse().Click(x, y))
	time.Sleep(500 * time.Millisecond)
	releaseOnce()

	onboardingStep(t, page, "payment-method")
	if sent := registrations.Load(); sent != 1 {
		t.Fatalf("double click sent %d registrations, want 1", sent)
	}
	sessionToken(t, page.Context())
}
