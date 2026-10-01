package e2e

import (
	"math"
	"net/url"
	"regexp"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

const navigationScrollStorageKey = "ts-dashboard:nav-scroll"

func TestNavigationScrollDesktopAndKeyboard(t *testing.T) {
	t.Parallel()
	page := newNavigationScrollPage(t)
	must(t, page.SetViewportSize(1440, 600))
	registerAndOnboard(t, page, newAccount("nav-scroll-desktop"))
	goToOK(t, page, "/auth/users")
	waitNavigationScrollReady(t, page)

	for _, destination := range []struct {
		path, label string
		keyboard    bool
	}{
		{"/auth/branding", "Branding", false},
		{"/auth/keys", "Management keys", true},
		{"/auth/branding", "Branding", false},
	} {
		link := page.Locator(`nav.ts-sidebar-content a[href="` + destination.path + `"]`)
		must(t, expect.Locator(link).ToHaveText(destination.label))
		must(t, link.ScrollIntoViewIfNeeded())
		if destination.keyboard {
			must(t, link.Focus())
			must(t, expect.Locator(link).ToBeFocused())
			// Branding is tall: only the menu offset should survive navigation.
			windowTop, err := page.Evaluate(`() => {
				window.scrollTo(0, document.documentElement.scrollHeight);
				return window.scrollY;
			}`)
			must(t, err)
			if navigationScrollNumber(t, windowTop) <= 0 {
				t.Fatal("keyboard navigation must start with the main window scrolled")
			}
		}
		viewportScreenshot(t, page, "ts330/desktop-before-"+destination.label)
		if destination.keyboard {
			must(t, page.Keyboard().Press("Enter"))
		} else {
			click(t, link)
		}
		waitNavigationScrollDestination(t, page, destination.path)
		top := navigationScrollActivationTop(t, page)
		if top <= 0 {
			t.Fatalf("%s activated without scrolling the short sidebar: %v", destination.path, top)
		}
		assertNavigationScrollTop(t, page, top)
		assertNavigationScrollActiveInViewport(t, page)
		windowTop, err := page.Evaluate(`() => window.scrollY`)
		must(t, err)
		if navigationScrollNumber(t, windowTop) != 0 {
			t.Fatalf("%s retained main window scroll %v", destination.path, windowTop)
		}
		assertNavigationScrollConsumed(t, page)
		viewportScreenshot(t, page, "ts330/desktop-after-"+destination.label)
	}
}

func TestNavigationScrollMobileReopensAtSavedOffset(t *testing.T) {
	t.Parallel()
	page := newNavigationScrollPage(t)
	must(t, page.SetViewportSize(390, 600))
	registerAndOnboard(t, page, newAccount("nav-scroll-mobile"))
	goToOK(t, page, "/auth/users")
	waitNavigationScrollReady(t, page)

	toggle := page.Locator("button.ts-fab-nav")
	click(t, toggle)
	must(t, expect.Locator(toggle).ToHaveAttribute("aria-expanded", "true"))
	link := page.Locator(`nav.ts-sidebar-content a[href="/auth/keys"]`)
	must(t, link.ScrollIntoViewIfNeeded())
	viewportScreenshot(t, page, "ts330/mobile-before-keys")
	click(t, link)
	waitNavigationScrollDestination(t, page, "/auth/keys")
	top := navigationScrollActivationTop(t, page)
	if top <= 0 {
		t.Fatalf("mobile Management keys activated at unscrolled offset %v", top)
	}
	must(t, expect.Locator(toggle).ToHaveAttribute("aria-expanded", "false"))
	must(t, expect.Locator(page.Locator(".ts-shell")).Not().ToHaveClass(regexp.MustCompile(`nav-open`)))

	click(t, toggle)
	must(t, expect.Locator(toggle).ToHaveAttribute("aria-expanded", "true"))
	assertNavigationScrollTop(t, page, top)
	assertNavigationScrollActiveInViewport(t, page)
	assertNavigationScrollConsumed(t, page)
	viewportScreenshot(t, page, "ts330/mobile-after-reopen-keys")
}

func TestNavigationScrollDoesNotLeakOutsideMenu(t *testing.T) {
	t.Parallel()
	page := newNavigationScrollPage(t)
	must(t, page.SetViewportSize(1440, 600))
	registerAndOnboard(t, page, newAccount("nav-scroll-scope"))

	for _, destination := range []struct {
		name, path string
		activate   func(*testing.T)
	}{
		{"direct navigation", "/auth/keys", func(t *testing.T) { goToOK(t, page, "/auth/keys") }},
		{"account menu", "/profile", func(t *testing.T) {
			click(t, page.Locator("details.ts-account > summary"))
			click(t, page.Locator(`details.ts-account a[href="/profile"]`))
		}},
	} {
		t.Run(destination.name, func(t *testing.T) {
			goToOK(t, page, "/auth/users")
			waitNavigationScrollReady(t, page)
			must(t, page.Locator(`nav.ts-sidebar-content a[href="/auth/keys"]`).ScrollIntoViewIfNeeded())
			if navigationScrollTop(t, page) <= 0 {
				t.Fatal("scope test needs a scrolled sidebar")
			}
			destination.activate(t)
			waitForPath(t, page, func(u *url.URL) bool { return u.Path == destination.path })
			waitNavigationScrollReady(t, page)
			assertNavigationScrollTop(t, page, 0)
			assertNavigationScrollConsumed(t, page)
			// Returning directly to Authentication must not resurrect an earlier offset.
			goToOK(t, page, "/auth/users")
			waitNavigationScrollDestination(t, page, "/auth/users")
			assertNavigationScrollTop(t, page, 0)
		})
	}
}

func TestNavigationScrollStorageIsValidatedAndOneShot(t *testing.T) {
	t.Parallel()
	page := newNavigationScrollPage(t)
	must(t, page.SetViewportSize(1440, 600))
	registerAndOnboard(t, page, newAccount("nav-scroll-storage"))

	for _, scenario := range []struct {
		name, value string
		want        float64
	}{
		{"matching destination", `{"href":"DESTINATION","top":120}`, 120},
		{"malformed JSON", `{`, 0},
		{"different query", `{"href":"DESTINATION?other=1","top":120}`, 0},
		{"negative offset", `{"href":"DESTINATION","top":-1}`, 0},
		{"string offset", `{"href":"DESTINATION","top":"120"}`, 0},
		{"nonfinite offset", `{"href":"DESTINATION","top":1e999}`, 0},
	} {
		t.Run(scenario.name, func(t *testing.T) {
			goToOK(t, page, "/auth/users")
			waitNavigationScrollReady(t, page)
			_, err := page.Evaluate(`value => sessionStorage.setItem('ts-dashboard:nav-scroll',
				value.replace('DESTINATION', new URL('/auth/keys', location.href).href))`, scenario.value)
			must(t, err)
			goToOK(t, page, "/auth/keys")
			waitNavigationScrollDestination(t, page, "/auth/keys")
			assertNavigationScrollTop(t, page, scenario.want)
			assertNavigationScrollConsumed(t, page)
			windowTop, err := page.Evaluate(`() => window.scrollY`)
			must(t, err)
			if navigationScrollNumber(t, windowTop) != 0 {
				t.Fatalf("sidebar restoration moved window scroll to %v", windowTop)
			}
			_, err = page.Reload()
			must(t, err)
			waitNavigationScrollDestination(t, page, "/auth/keys")
			assertNavigationScrollTop(t, page, 0)
		})
	}
}

func TestNavigationScrollBlockedStorageStillNavigates(t *testing.T) {
	t.Parallel()
	page := newNavigationScrollPage(t)
	must(t, page.SetViewportSize(1440, 600))
	registerAndOnboard(t, page, newAccount("nav-scroll-blocked"))
	must(t, page.AddInitScript(playwright.Script{Content: new(`
		for (const method of ['getItem', 'setItem', 'removeItem']) {
			const original = Storage.prototype[method];
			Storage.prototype[method] = function(key, ...args) {
				if (key === 'ts-dashboard:nav-scroll') throw new DOMException('Storage blocked', 'SecurityError');
				return original.call(this, key, ...args);
			};
		}`)}))
	goToOK(t, page, "/auth/users")
	waitNavigationScrollReady(t, page)
	link := page.Locator(`nav.ts-sidebar-content a[href="/auth/keys"]`)
	must(t, link.ScrollIntoViewIfNeeded())
	click(t, link)
	waitNavigationScrollDestination(t, page, "/auth/keys")
	assertNavigationScrollTop(t, page, 0)
	// watchedPage also rejects console errors and uncaught errors from either hook.
}

func newNavigationScrollPage(t *testing.T) playwright.Page {
	t.Helper()
	page := newPage(t)
	must(t, page.AddInitScript(playwright.Script{Content: new(`
		document.addEventListener('datastar-ready', () => {
			document.documentElement.dataset.datastarReady = 'true';
		}, {once: true});
		// Capture after browser/Playwright scrolling, but before the save handler.
		document.addEventListener('click', event => {
			const nav = document.querySelector('nav.ts-sidebar-content');
			const link = event.target.closest?.('a[href]');
			if (nav?.contains(link)) {
				sessionStorage.setItem('ts-dashboard:e2e-nav-activation', String(nav.scrollTop));
			}
		}, {capture: true});
		`)}))
	return page
}

func waitNavigationScrollReady(t *testing.T, page playwright.Page) {
	t.Helper()
	must(t, expect.Locator(page.Locator("html")).ToHaveAttribute("data-datastar-ready", "true"))
	_, err := page.Evaluate(`async () => {
		await document.fonts.ready;
		await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
	}`)
	must(t, err)
}

func waitNavigationScrollDestination(t *testing.T, page playwright.Page, path string) {
	t.Helper()
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == path })
	waitNavigationScrollReady(t, page)
	link := page.Locator(`nav.ts-sidebar-content a[href="` + path + `"]`)
	must(t, expect.Locator(link).ToHaveAttribute("aria-current", "page"))
	must(t, expect.Locator(page.Locator("details.ts-nav-collapsible").Filter(playwright.LocatorFilterOptions{Has: page.Locator(`a[href="` + path + `"]`)})).ToHaveAttribute("open", ""))
}

func assertNavigationScrollActiveInViewport(t *testing.T, page playwright.Page) {
	t.Helper()
	eventually(t, "active child inside sidebar viewport", func() bool {
		value, err := page.Locator("nav.ts-sidebar-content").Evaluate(`nav => {
			const link = nav.querySelector('a[aria-current="page"]');
			if (!link) return false;
			const bounds = nav.getBoundingClientRect(), child = link.getBoundingClientRect();
			return child.top >= bounds.top - 1 && child.bottom <= bounds.bottom + 1 &&
				child.left >= bounds.left - 1 && child.right <= bounds.right + 1;
		}`, nil)
		must(t, err)
		return value.(bool)
	})
}

func navigationScrollActivationTop(t *testing.T, page playwright.Page) float64 {
	t.Helper()
	value, err := page.Evaluate(`() => Number(sessionStorage.getItem('ts-dashboard:e2e-nav-activation'))`)
	must(t, err)
	return navigationScrollNumber(t, value)
}

func navigationScrollTop(t *testing.T, page playwright.Page) float64 {
	t.Helper()
	value, err := page.Locator("nav.ts-sidebar-content").Evaluate(`nav => nav.scrollTop`, nil)
	must(t, err)
	return navigationScrollNumber(t, value)
}

func navigationScrollNumber(t *testing.T, value any) float64 {
	t.Helper()
	switch value := value.(type) {
	case int:
		return float64(value)
	case float64:
		return value
	default:
		t.Fatalf("unexpected scroll offset type %T", value)
		return 0
	}
}

func assertNavigationScrollTop(t *testing.T, page playwright.Page, want float64) {
	t.Helper()
	eventually(t, "sidebar scroll offset", func() bool {
		return math.Abs(navigationScrollTop(t, page)-want) <= 1
	})
}

func assertNavigationScrollConsumed(t *testing.T, page playwright.Page) {
	t.Helper()
	value, err := page.Evaluate(`key => sessionStorage.getItem(key)`, navigationScrollStorageKey)
	must(t, err)
	if value != nil {
		t.Fatalf("one-shot sidebar scroll entry was not consumed: %v", value)
	}
}
