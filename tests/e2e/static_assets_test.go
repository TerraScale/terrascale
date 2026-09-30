package e2e

import (
	"net/url"
	"path"
	"regexp"
	"strings"
	"sync"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// TestStaticAssetsAreCachedAsImmutable loads the signed-out, signed-in and
// not-found layouts and requires every same-origin file they fetch to use a
// content-hashed URL the browser caches without revalidating.
func TestStaticAssetsAreCachedAsImmutable(t *testing.T) {
	t.Parallel()
	context := newContext(t, baseURL)
	var (
		mu       sync.Mutex
		policies = map[string]string{}
	)
	context.OnResponse(func(response playwright.Response) {
		if response.Request().ResourceType() == "document" || !strings.HasPrefix(response.URL(), baseURL+"/") {
			return
		}
		mu.Lock()
		defer mu.Unlock()
		policies[response.URL()] = response.Headers()["cache-control"]
	})
	load := func(page playwright.Page, path string, status int) {
		t.Helper()
		response, err := page.Goto(path, playwright.PageGotoOptions{WaitUntil: playwright.WaitUntilStateNetworkidle})
		must(t, err)
		if response.Status() != status {
			t.Fatalf("GET %s = %d, want %d", path, response.Status(), status)
		}
	}
	page := watchedPage(t, context)
	load(page, "/login", 200)
	// Every navigation must load Datastar from the dashboard, independent of
	// CDN availability, with the same immutable caching as the other assets.
	must(t, expect.Locator(page.Locator(`script[type="module"][integrity]`)).ToHaveAttribute("src", assetURL("/js/datastar.js")))
	load(page, "/onboarding", 200)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	for _, path := range []string{"/", "/databases", "/organization"} {
		load(page, path, 200)
	}
	// The browser logs the 404 status itself, so this page is not watched.
	missing, err := context.NewPage()
	must(t, err)
	load(missing, "/no-such-page", 404)

	mu.Lock()
	defer mu.Unlock()
	kinds := map[string]bool{}
	for raw, policy := range policies {
		if !strings.Contains(policy, "immutable") {
			t.Errorf("%s is cached as %q; link it through static.URL", raw, policy)
		}
		parsed, err := url.Parse(raw)
		must(t, err)
		kinds[path.Ext(parsed.Path)] = true
	}
	for _, kind := range []string{".css", ".js", ".woff2", ".svg", ".webp"} {
		if !kinds[kind] {
			t.Errorf("no %s file was loaded; the pages no longer exercise it", kind)
		}
	}
}

// assetURL matches the content-hashed URL static.URL links the file at path by.
func assetURL(path string) *regexp.Regexp {
	return regexp.MustCompile(`^` + regexp.QuoteMeta(path) + `\?v=[0-9a-f]{12}$`)
}
