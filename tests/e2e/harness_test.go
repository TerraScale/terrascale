package e2e

import (
	"crypto/rand"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

const sessionCookie = "ts_dashboard_session"

type credentials struct {
	Email, Password, DisplayName string
}

// newAccount returns unique credentials so parallel tests never share state.
func newAccount(label string) credentials {
	return credentials{
		Email:       fmt.Sprintf("e2e-%s-%s@example.test", label, strings.ToLower(rand.Text()[:12])),
		Password:    "E2e-journey-123!",
		DisplayName: "E2E " + label,
	}
}

// newContext opens an isolated browser context that is closed with the test.
func newContext(t *testing.T, base string) playwright.BrowserContext {
	t.Helper()
	context, err := browser.NewContext(playwright.BrowserNewContextOptions{BaseURL: playwright.String(base)})
	must(t, err)
	t.Cleanup(func() { _ = context.Close() })
	return context
}

// newPage opens a page in a fresh context and fails the test on any browser
// console error or uncaught page error.
func newPage(t *testing.T) playwright.Page {
	t.Helper()
	return watchedPage(t, newContext(t, baseURL))
}

func watchedPage(t *testing.T, context playwright.BrowserContext) playwright.Page {
	t.Helper()
	page, err := context.NewPage()
	must(t, err)
	var (
		mu     sync.Mutex
		errors []string
	)
	record := func(message string) {
		mu.Lock()
		defer mu.Unlock()
		errors = append(errors, message)
	}
	page.OnConsole(func(message playwright.ConsoleMessage) {
		if message.Type() == "error" {
			record("console: " + message.Text())
		}
	})
	page.OnPageError(func(err error) { record("pageerror: " + err.Error()) })
	t.Cleanup(func() {
		mu.Lock()
		defer mu.Unlock()
		if len(errors) > 0 {
			t.Errorf("browser console and uncaught page errors:\n%s", strings.Join(errors, "\n"))
		}
	})
	return page
}

func must(t *testing.T, err error) {
	t.Helper()
	if err != nil {
		t.Fatal(err)
	}
}

func goTo(t *testing.T, page playwright.Page, path string) playwright.Response {
	t.Helper()
	response, err := page.Goto(path)
	must(t, err)
	return response
}

func waitForPath(t *testing.T, page playwright.Page, match func(*url.URL) bool) {
	t.Helper()
	must(t, page.WaitForURL(func(raw string) bool {
		parsed, err := url.Parse(raw)
		return err == nil && match(parsed)
	}))
}

func currentPath(t *testing.T, page playwright.Page) string {
	t.Helper()
	parsed, err := url.Parse(page.URL())
	must(t, err)
	return parsed.Path
}

func byRole(scope interface {
	GetByRole(playwright.AriaRole, ...playwright.LocatorGetByRoleOptions) playwright.Locator
}, role playwright.AriaRole, name string, exact ...bool) playwright.Locator {
	options := playwright.LocatorGetByRoleOptions{Name: name}
	if len(exact) > 0 {
		options.Exact = playwright.Bool(exact[0])
	}
	return scope.GetByRole(role, options)
}

func pageByRole(page playwright.Page, role playwright.AriaRole, name string, exact ...bool) playwright.Locator {
	options := playwright.PageGetByRoleOptions{Name: name}
	if len(exact) > 0 {
		options.Exact = playwright.Bool(exact[0])
	}
	return page.GetByRole(role, options)
}

func click(t *testing.T, locator playwright.Locator) {
	t.Helper()
	must(t, locator.Click())
}

func fill(t *testing.T, locator playwright.Locator, value string) {
	t.Helper()
	must(t, locator.Fill(value))
}

func selectOption(t *testing.T, locator playwright.Locator, value string) {
	t.Helper()
	_, err := locator.SelectOption(playwright.SelectOptionValues{Values: &[]string{value}})
	must(t, err)
}

func visible(t *testing.T, locator playwright.Locator) {
	t.Helper()
	must(t, expect.Locator(locator).ToBeVisible())
}

func sessionToken(t *testing.T, context playwright.BrowserContext) string {
	t.Helper()
	cookies, err := context.Cookies()
	must(t, err)
	for _, cookie := range cookies {
		if cookie.Name == sessionCookie {
			return cookie.Value
		}
	}
	t.Fatal("account creation must establish a dashboard session cookie")
	return ""
}

// screenshot captures desktop and mobile full-page shots when
// DASHBOARD_E2E_SCREENSHOTS names an output directory.
func screenshot(t *testing.T, page playwright.Page, name string) {
	t.Helper()
	dir := os.Getenv("DASHBOARD_E2E_SCREENSHOTS")
	if dir == "" {
		return
	}
	must(t, os.MkdirAll(dir, 0o755))
	original := page.ViewportSize()
	for _, size := range []struct {
		label         string
		width, height int
	}{{"desktop", 1440, 900}, {"mobile", 390, 844}} {
		must(t, page.SetViewportSize(size.width, size.height))
		_, err := page.Screenshot(playwright.PageScreenshotOptions{
			Path: playwright.String(filepath.Join(dir, name+"-"+size.label+".png")), FullPage: playwright.Bool(true),
		})
		must(t, err)
	}
	if original != nil {
		must(t, page.SetViewportSize(original.Width, original.Height))
	}
}
