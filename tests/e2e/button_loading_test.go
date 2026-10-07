package e2e

import (
	"net/http"
	"net/url"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/mxschmitt/playwright-go"
)

func TestNativeButtonLinkLoadingPreservesContentAndRestoresARIA(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("loading-link"))
	link := page.Locator(`a.ts-btn[href="/databases"]`)
	icon := link.Locator(":scope > svg:not(.ts-btn-spinner)")
	label := link.Locator(":scope > span")
	width, err := link.Evaluate(`link => {
		link.setAttribute('aria-busy', 'false');
		link.setAttribute('aria-disabled', 'false');
		// Cancel after the loading handler so the real page remains inspectable.
		window.loadingClicks = 0;
		window.addEventListener('click', event => {
			if (event.target.closest('a.ts-btn') === link) {
				window.loadingClicks++;
				event.preventDefault();
			}
		});
		return link.getBoundingClientRect().width;
	}`, nil)
	must(t, err)
	for _, cleanup := range []string{
		`window.dispatchEvent(new KeyboardEvent('keydown', {key: 'Escape'}))`,
		`window.dispatchEvent(new PageTransitionEvent('pageshow', {persisted: true}))`,
	} {
		click(t, label)
		assertButtonLoading(t, link)
		must(t, expect.Locator(icon).ToBeHidden())
		must(t, expect.Locator(label).ToBeVisible())
		must(t, expect.Locator(label).ToHaveText("New database"))
		after, err := link.Evaluate("link => link.getBoundingClientRect().width", nil)
		must(t, err)
		if after != width {
			t.Fatalf("loading link width = %v, want %v", after, width)
		}
		must(t, label.DispatchEvent("click", nil))
		_, err = page.Evaluate(cleanup)
		must(t, err)
		assertButtonIdle(t, link)
		must(t, expect.Locator(icon).ToBeVisible())
		must(t, expect.Locator(link).ToHaveAttribute("aria-busy", "false"))
		must(t, expect.Locator(link).ToHaveAttribute("aria-disabled", "false"))
	}
	clicks, err := page.Evaluate("window.loadingClicks")
	must(t, err)
	if clicks != 2 {
		t.Fatalf("link clicks reaching navigation = %v, want 2", clicks)
	}
}

func TestNativeButtonLinkLoadingExcludesNonNavigatingClicks(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("loading-excluded-link"))
	link := page.Locator(`a.ts-btn[href="/databases"]`)
	for _, test := range []struct {
		name  string
		setup string
	}{
		{"control", `options.ctrlKey = true`},
		{"meta", `options.metaKey = true`},
		{"shift", `options.shiftKey = true`},
		{"alt", `options.altKey = true`},
		{"middle", `options.button = 1`},
		{"download", `link.setAttribute('download', '')`},
		{"hash", `link.href = location.pathname + location.search + '#details'`},
		{"new-tab", `link.target = '_blank'`},
		{"external", `link.href = 'https://example.test/login'`},
		{"canceled", `link.addEventListener('click', event => event.preventDefault(), {once: true})`},
	} {
		t.Run(test.name, func(t *testing.T) {
			_, err := link.Evaluate(`link => {
				link.href = '/databases';
				link.removeAttribute('download');
				link.removeAttribute('target');
				const options = {bubbles: true, cancelable: true, button: 0};
				`+test.setup+`;
				// Suppress browser navigation after the shared handler runs.
				window.addEventListener('click', event => event.preventDefault(), {once: true});
				link.dispatchEvent(new MouseEvent('click', options));
				link.href = '/databases';
			}`, nil)
			must(t, err)
			assertButtonIdle(t, link)
		})
	}
}

func TestOnboardingLoadingReplacesDecorativeArrow(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOpenPaymentStep(t, page, newAccount("loading-arrow"))
	form := onboardingStep(t, page, "payment-method")
	button := byRole(form, *playwright.AriaRoleButton, "Continue to checkout")
	arrow := button.Locator(`:scope > span[aria-hidden="true"]`)
	must(t, expect.Locator(arrow).ToHaveText("→"))
	_, err := page.Evaluate(`() => window.addEventListener('submit', event => event.preventDefault())`)
	must(t, err)
	click(t, button)
	assertButtonLoading(t, button)
	must(t, expect.Locator(arrow).ToBeHidden())
	must(t, expect.Locator(button).ToHaveAccessibleName("Continue to checkout"))
	must(t, page.Keyboard().Press("Escape"))
	assertButtonIdle(t, button)
	must(t, expect.Locator(arrow).ToBeVisible())
}

func TestNativeDefaultSubmitLoadingPrefersPrimaryAndPreservesSubmission(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("loading-default-submit"))
	goToOK(t, page, "/auth/emails?template=sign_in_code")
	fill(t, page.Locator("#email-subject"), "Loading regression {{ code }}")
	click(t, page.Locator(`button[name="intent"][value="save"]`))
	reset := page.Locator(`button[name="intent"][value="reset"]`)
	save := page.Locator(`button[name="intent"][value="save"]`)
	must(t, expect.Locator(reset).ToBeVisible())
	form := save.Locator("xpath=ancestor::form")
	before, err := reset.Evaluate(`reset => !!(reset.compareDocumentPosition(reset.form.querySelector('button[value="save"]')) & Node.DOCUMENT_POSITION_FOLLOWING)`, nil)
	must(t, err)
	if before != true {
		t.Fatal("regression fixture requires Reset before Save in the form")
	}
	// A 204 keeps the original document so pending feedback is observable,
	// while the browser still performs its real native POST serialization.
	must(t, page.Route("**/api/v1/auth-management/emails", func(route playwright.Route) {
		_ = route.Fulfill(playwright.RouteFulfillOptions{Status: new(http.StatusNoContent)})
	}))
	for _, intent := range []string{"", "reset", "save"} {
		t.Run("intent="+intent, func(t *testing.T) {
			t.Cleanup(func() { _ = page.Keyboard().Press("Escape") })
			response, err := page.ExpectResponse("**/api/v1/auth-management/emails", func() error {
				_, err := form.Evaluate(`(form, intent) => form.requestSubmit(intent ? form.querySelector('button[value="' + intent + '"]') : undefined)`, intent)
				return err
			})
			must(t, err)
			request := response.Request()
			if !request.IsNavigationRequest() || request.Method() != http.MethodPost {
				t.Fatalf("submission must be a native POST, got %s %s", request.Method(), request.URL())
			}
			body, err := request.PostData()
			must(t, err)
			values, err := url.ParseQuery(body)
			must(t, err)
			if values.Has("intent") != (intent != "") || values.Get("intent") != intent || values.Get("template") != "sign_in_code" || values.Get("subject") != "Loading regression {{ code }}" {
				t.Fatalf("native submission lost fields or submitter value: %v", values)
			}
			active, inactive := save, reset
			if intent == "reset" {
				active, inactive = reset, save
			}
			assertButtonLoading(t, active)
			assertButtonIdle(t, inactive)
			must(t, expect.Locator(active).ToHaveJSProperty("disabled", false))
			must(t, page.Keyboard().Press("Escape"))
			assertButtonIdle(t, active)
		})
	}
}

func TestActionButtonsShowLoadingForNativeAndKeyboardSubmissions(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	databasePath := sampleDatabase(t, page)
	goToOK(t, page, databasePath+"/sql?collection=orders")
	editor := codeEditor(t, page, "sql")
	form := page.Locator(`form[action="/api/v1/dashboard/data/sql"]`)
	run := pageByRole(page, *playwright.AriaRoleButton, "Run query")
	check := pageByRole(page, *playwright.AriaRoleButton, "Check SQL")
	viewportScreenshot(t, page, "ts658/sql-idle")
	width, err := run.Evaluate("button => button.getBoundingClientRect().width", nil)
	must(t, err)
	// Keep the native document in place so its pending UI can be inspected.
	// This listener runs after the shared window submit listener.
	_, err = page.Evaluate(`() => {
		window.loadingSubmissions = 0;
		window.addEventListener('submit', event => {
			window.loadingSubmissions++;
			event.preventDefault();
		});
	}`)
	must(t, err)

	click(t, run)
	assertButtonLoading(t, run)
	viewportScreenshot(t, page, "ts658/sql-loading")
	must(t, expect.Locator(check.Locator(".ts-btn-spinner")).ToHaveCount(0))
	after, err := run.Evaluate("button => button.getBoundingClientRect().width", nil)
	must(t, err)
	if after != width {
		t.Fatalf("loading button width = %v, want %v", after, width)
	}
	// Dispatch bypasses the accessibility disabled state to exercise the
	// repeat-click guard itself, then try a programmatic repeated submit.
	must(t, run.DispatchEvent("click", nil))
	_, err = form.Evaluate("form => form.requestSubmit()", nil)
	must(t, err)
	sent, err := page.Evaluate("window.loadingSubmissions")
	must(t, err)
	if sent != 1 {
		t.Fatalf("repeated submissions = %v, want 1", sent)
	}
	must(t, page.Keyboard().Press("Escape"))
	assertButtonIdle(t, run)

	click(t, check)
	assertButtonLoading(t, check)
	viewportScreenshot(t, page, "ts658/check-sql-loading")
	must(t, expect.Locator(run.Locator(".ts-btn-spinner")).ToHaveCount(0))
	_, err = page.Evaluate("window.dispatchEvent(new PageTransitionEvent('pageshow', {persisted: true}))")
	must(t, err)
	assertButtonIdle(t, check)

	click(t, editor.Locator(".view-lines"))
	must(t, page.Keyboard().Press("ControlOrMeta+Enter"))
	assertButtonLoading(t, run)
	must(t, expect.Locator(check.Locator(".ts-btn-spinner")).ToHaveCount(0))
	must(t, page.EmulateMedia(playwright.PageEmulateMediaOptions{ReducedMotion: playwright.ReducedMotionReduce}))
	must(t, expect.Locator(run.Locator(".ts-btn-spinner")).ToHaveCSS("animation-iteration-count", "1"))
}

func TestActionButtonLoadingEndsAfterDatastarSuccessAndError(t *testing.T) {
	t.Parallel()
	page := formErrorPage(t)
	registerAndOnboard(t, page, newAccount("button-loading"))
	goToOK(t, page, "/auth/emails?template=sign_in_code")
	button := page.Locator(`button:has-text("Send test to")`)
	viewportScreenshot(t, page, "ts658/email-idle")
	fill(t, page.Locator("#email-reply-to"), "invalid-email")
	click(t, button)
	assertButtonIdle(t, button)
	fill(t, page.Locator("#email-reply-to"), "")
	for _, status := range []int{http.StatusOK, http.StatusUnprocessableEntity} {
		var sent atomic.Int32
		release := make(chan struct{})
		releaseOnce := sync.OnceFunc(func() { close(release) })
		t.Cleanup(releaseOnce)
		must(t, page.Route("**/api/v1/auth-management/emails/test", func(route playwright.Route) {
			sent.Add(1)
			select {
			case <-release:
			case <-time.After(10 * time.Second):
			}
			_ = route.Fulfill(playwright.RouteFulfillOptions{
				Status: new(status), ContentType: new("text/event-stream"),
				Body: "event: datastar-patch-elements\ndata: elements <p id=\"email-test-status\">Request completed.</p>\n\n",
			})
		}))
		click(t, button)
		assertButtonLoading(t, button)
		if status == http.StatusOK {
			viewportScreenshot(t, page, "ts658/email-loading")
		}
		must(t, button.DispatchEvent("click", nil))
		must(t, page.Keyboard().Press("Escape"))
		// Escape cancels native navigation, but an active fetch stays busy.
		assertButtonLoading(t, button)
		releaseOnce()
		assertButtonIdle(t, button)
		must(t, expect.Locator(page.Locator("#email-test-status")).ToHaveText("Request completed."))
		if count := sent.Load(); count != 1 {
			t.Fatalf("status %d: sent %d requests, want 1", status, count)
		}
		must(t, page.Unroute("**/api/v1/auth-management/emails/test"))
	}

	// Abort an actual in-flight fetch; Datastar must finish its lifecycle
	// without a response or patch, restoring feedback and earlier ARIA values.
	{
		_, err := button.Evaluate(`button => {
			window.loadingAbort = new AbortController();
			button.setAttribute('aria-busy', 'false');
			button.setAttribute('aria-disabled', 'false');
			button.setAttribute('data-on:click', "@post('/api/v1/auth-management/emails/test', {contentType:'form', requestCancellation:window.loadingAbort})");
		}`, nil)
		must(t, err)
		release := make(chan struct{})
		releaseOnce := sync.OnceFunc(func() { close(release) })
		t.Cleanup(releaseOnce)
		var sent atomic.Int32
		must(t, page.Route("**/api/v1/auth-management/emails/test", func(route playwright.Route) {
			sent.Add(1)
			select {
			case <-release:
			case <-time.After(10 * time.Second):
			}
			_ = route.Fulfill(playwright.RouteFulfillOptions{Status: new(200), ContentType: new("text/event-stream"), Body: ""})
		}))
		click(t, button)
		assertButtonLoading(t, button)
		eventually(t, "the test email fetch to start", func() bool { return sent.Load() == 1 })
		_, err = page.Evaluate("window.loadingAbort.abort()")
		must(t, err)
		assertButtonIdle(t, button)
		must(t, expect.Locator(button).ToHaveAttribute("aria-busy", "false"))
		must(t, expect.Locator(button).ToHaveAttribute("aria-disabled", "false"))
		releaseOnce()
		must(t, page.Unroute("**/api/v1/auth-management/emails/test"))
	}

	// Exercise Datastar's form-origin lifecycle as well as its button-origin
	// lifecycle, using the same endpoint with the form's native Save control.
	form := button.Locator("xpath=ancestor::form")
	_, err := form.Evaluate(`form => form.setAttribute('data-on:submit', "@post('/api/v1/auth-management/emails/test', {contentType:'form'})")`, nil)
	must(t, err)
	save := form.Locator(`button[value="save"]`)
	release := make(chan struct{})
	releaseOnce := sync.OnceFunc(func() { close(release) })
	t.Cleanup(releaseOnce)
	must(t, page.Route("**/api/v1/auth-management/emails/test", func(route playwright.Route) {
		select {
		case <-release:
		case <-time.After(10 * time.Second):
		}
		_ = route.Fulfill(playwright.RouteFulfillOptions{Status: new(200), ContentType: new("text/event-stream"), Body: ""})
	}))
	click(t, save)
	assertButtonLoading(t, save)
	must(t, expect.Locator(button.Locator(".ts-btn-spinner")).ToHaveCount(0))
	releaseOnce()
	assertButtonIdle(t, save)
}

func TestInvalidNativeFormDoesNotShowButtonLoading(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	goToOK(t, page, "/onboarding")
	button := page.Locator(`form[data-submit-once] button[type="submit"]`)
	click(t, button)
	assertButtonIdle(t, button)
}

func assertButtonLoading(t *testing.T, button playwright.Locator) {
	t.Helper()
	must(t, expect.Locator(button).ToHaveAttribute("aria-busy", "true"))
	must(t, expect.Locator(button).ToHaveAttribute("aria-disabled", "true"))
	must(t, expect.Locator(button).ToHaveCSS("cursor", "progress"))
	must(t, expect.Locator(button.Locator(".ts-btn-spinner")).ToBeVisible())
	must(t, expect.Locator(button.Locator(".ts-btn-spinner")).ToHaveCSS("animation-name", "ts-button-spin"))
	must(t, expect.Locator(button.Locator(".ts-btn-idle-icon")).ToBeHidden())
}

func assertButtonIdle(t *testing.T, button playwright.Locator) {
	t.Helper()
	must(t, expect.Locator(button.Locator(".ts-btn-spinner")).ToHaveCount(0))
	must(t, expect.Locator(button).Not().ToHaveAttribute("aria-busy", "true"))
	must(t, expect.Locator(button).Not().ToHaveAttribute("aria-disabled", "true"))
}
