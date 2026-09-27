package e2e

import (
	json "encoding/json/v2"
	"fmt"
	"maps"
	"net/url"
	"path"
	"slices"
	"strings"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// weightBudget caps, per kind of resource, the compressed bytes a cold load
// of a page transfers.
type weightBudget map[string]int

// pageBudget sits about a fifth above what the pages transfer today, so a
// heavier stylesheet, font, script or image fails here instead of shipping.
var pageBudget = weightBudget{"document": 25_000, "stylesheet": 20_000, "font": 65_000, "script": 30_000, "image": 15_000}

// codeEditorBudget adds Monaco, which pages with a code editor download once
// they have loaded, and its icon font, fetched once the editor shows an icon.
var codeEditorBudget = weightBudget{"document": 25_000, "stylesheet": 20_000, "font": 140_000, "script": 1_350_000, "image": 15_000}

// collectTransfers lists the navigation and every resource with the
// compressed body size the browser received.
const collectTransfers = `() => JSON.stringify([...performance.getEntriesByType("navigation"), ...performance.getEntriesByType("resource")]
  .map(entry => ({ url: entry.name, bytes: entry.encodedBodySize, document: entry.entryType === "navigation" })))`

type transfer struct {
	URL      string `json:"url"`
	Bytes    int    `json:"bytes"`
	Document bool   `json:"document"`
}

func TestPageWeightStaysWithinBudget(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	sqlConsole := sqlConsolePath(t, page)
	session, err := page.Context().StorageState()
	must(t, err)

	for _, tc := range []struct {
		name, path string
		signedIn   bool
		budget     weightBudget
	}{
		{"login", "/login", false, pageBudget},
		{"home", "/", true, pageBudget},
		{"databases", "/databases", true, pageBudget},
		{"usage", "/usage", true, pageBudget},
		{"organization", "/organization", true, pageBudget},
		{"sql-console", sqlConsole, true, codeEditorBudget},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			options := playwright.BrowserNewContextOptions{BaseURL: new(baseURL)}
			if tc.signedIn {
				options.StorageState = session.ToOptionalStorageState()
			}
			context, err := browser.NewContext(options)
			must(t, err)
			t.Cleanup(func() { _ = context.Close() })
			page := watchedPage(t, context)
			response, err := page.Goto(tc.path, playwright.PageGotoOptions{WaitUntil: playwright.WaitUntilStateNetworkidle})
			must(t, err)
			if !response.Ok() || currentPath(t, page) != tc.path {
				t.Fatalf("GET %s ended at %s with %d", tc.path, page.URL(), response.Status())
			}
			if tc.path == sqlConsole {
				visible(t, page.Locator("ts-code-editor[data-ready] .monaco-editor").First())
			}
			raw, err := page.Evaluate(collectTransfers)
			must(t, err)
			var transfers []transfer
			must(t, json.Unmarshal([]byte(raw.(string)), &transfers))

			totals := map[string]int{}
			for _, transfer := range transfers {
				kind, err := resourceKind(transfer)
				if err != nil {
					t.Error(err)
					continue
				}
				totals[kind] += transfer.Bytes
			}
			for _, kind := range slices.Sorted(maps.Keys(totals)) {
				if bytes, limit := totals[kind], tc.budget[kind]; bytes > limit {
					t.Errorf("%s transfers %d bytes of %s, budget %d", tc.path, bytes, kind, limit)
				}
			}
			if t.Failed() || testing.Verbose() {
				for _, transfer := range transfers {
					t.Logf("%8d %s", transfer.Bytes, transfer.URL)
				}
			}
		})
	}
}

// resourceKind groups a transfer by what it is, read from its URL.
func resourceKind(transfer transfer) (string, error) {
	if transfer.Document {
		return "document", nil
	}
	parsed, err := url.Parse(transfer.URL)
	if err != nil {
		return "", err
	}
	switch strings.ToLower(path.Ext(parsed.Path)) {
	case ".css":
		return "stylesheet", nil
	case ".woff2", ".ttf":
		return "font", nil
	case ".js":
		return "script", nil
	case ".svg", ".webp", ".png", ".jpg":
		return "image", nil
	}
	return "", fmt.Errorf("unbudgeted resource %s", transfer.URL)
}
