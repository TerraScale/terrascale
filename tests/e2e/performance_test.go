package e2e

import (
	"cmp"
	json "encoding/json/v2"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"testing"
	"time"

	"github.com/mxschmitt/playwright-go"
)

// TestPerformance records Core Web Vitals for the main pages on desktop and
// throttled mobile profiles. It only runs when DASHBOARD_E2E_PERF=1; set
// PERF_RUNS, PERF_OUTPUT and PERF_BASELINE to tune runs and compare results.
func TestPerformance(t *testing.T) {
	if os.Getenv("DASHBOARD_E2E_PERF") != "1" {
		t.Skip("set DASHBOARD_E2E_PERF=1 to measure page performance")
	}
	runs, err := strconv.Atoi(cmp.Or(os.Getenv("PERF_RUNS"), "5"))
	if err != nil || runs < 1 {
		t.Fatal("PERF_RUNS must be a positive integer")
	}

	var samples []perfSample
	for _, profile := range []string{"desktop", "mobile"} {
		for _, path := range []string{"/login", "/", "/databases", "/data", "/settings", "/usage"} {
			for run := range runs {
				samples = append(samples, measure(t, profile, path, run)...)
			}
		}
	}

	summary := summarize(samples)
	for _, row := range summary {
		t.Logf("%-28s lcp=%.0f cls=%.3f ttfb=%.0f dcl=%.0f bytes=%.0f longTasks=%.0f",
			row.Key, row.LCP, row.CLS, row.TTFB, row.DOMContentLoaded, row.Bytes, row.LongTasks)
	}
	output := cmp.Or(os.Getenv("PERF_OUTPUT"), "../../test-results/performance.json")
	must(t, os.MkdirAll(filepath.Dir(output), 0o755))
	encoded, err := json.Marshal(perfReport{BaseURL: baseURL, Runs: runs, Summary: summary, Results: samples})
	must(t, err)
	must(t, os.WriteFile(output, encoded, 0o644))

	if path := os.Getenv("PERF_BASELINE"); path != "" {
		compareBaseline(t, path, summary)
	}
}

type perfSample struct {
	Profile          string  `json:"profile"`
	Path             string  `json:"path"`
	Run              int     `json:"run"`
	Cache            string  `json:"cache"`
	LCP              float64 `json:"lcp"`
	CLS              float64 `json:"cls"`
	LongTasks        float64 `json:"longTasks"`
	TTFB             float64 `json:"ttfb"`
	DOMContentLoaded float64 `json:"domContentLoaded"`
	Bytes            float64 `json:"bytes"`
}

type perfSummary struct {
	Key              string  `json:"key"`
	LCP              float64 `json:"lcp"`
	CLS              float64 `json:"cls"`
	TTFB             float64 `json:"ttfb"`
	DOMContentLoaded float64 `json:"domContentLoaded"`
	Bytes            float64 `json:"bytes"`
	LongTasks        float64 `json:"longTasks"`
}

type perfReport struct {
	BaseURL string        `json:"baseURL"`
	Runs    int           `json:"runs"`
	Summary []perfSummary `json:"summary"`
	Results []perfSample  `json:"results"`
}

const perfObservers = `() => {
  window.perf = { lcp: 0, cls: 0, longTasks: 0 };
  new PerformanceObserver(list => {
    for (const entry of list.getEntries()) window.perf.lcp = entry.startTime;
  }).observe({ type: "largest-contentful-paint", buffered: true });
  new PerformanceObserver(list => {
    for (const entry of list.getEntries()) if (!entry.hadRecentInput) window.perf.cls += entry.value;
  }).observe({ type: "layout-shift", buffered: true });
  new PerformanceObserver(list => {
    for (const entry of list.getEntries()) window.perf.longTasks += entry.duration;
  }).observe({ type: "longtask", buffered: true });
}`

const perfCollect = `() => {
  const nav = performance.getEntriesByType("navigation")[0];
  const resources = performance.getEntriesByType("resource");
  return JSON.stringify({ ...window.perf, ttfb: nav.responseStart, domContentLoaded: nav.domContentLoadedEventEnd,
    bytes: nav.transferSize + resources.reduce((n, r) => n + r.transferSize, 0) });
}`

type cdpCommand struct {
	method string
	params map[string]any
}

// mobileThrottling emulates a mid-range phone on a slow cellular network.
var mobileThrottling = []cdpCommand{
	{"Emulation.setCPUThrottlingRate", map[string]any{"rate": 4}},
	{"Network.emulateNetworkConditions", map[string]any{"offline": false, "latency": 80, "downloadThroughput": 200_000, "uploadThroughput": 100_000}},
}

func measure(t *testing.T, profile, path string, run int) []perfSample {
	t.Helper()
	viewport := &playwright.Size{Width: 1440, Height: 900}
	if profile == "mobile" {
		viewport = &playwright.Size{Width: 390, Height: 844}
	}
	context, err := browser.NewContext(playwright.BrowserNewContextOptions{BaseURL: new(baseURL), Viewport: viewport})
	must(t, err)
	defer context.Close()
	page, err := context.NewPage()
	must(t, err)
	if path != "/login" {
		login(t, page, sampleAccount.Email, sampleAccount.Password)
	}
	must(t, page.BringToFront())
	cdp, err := context.NewCDPSession(page)
	must(t, err)
	commands := []cdpCommand{{method: "Network.enable"}}
	if profile == "mobile" {
		commands = append(commands, mobileThrottling...)
	}
	for _, command := range commands {
		_, err := cdp.Send(command.method, command.params)
		must(t, err)
	}
	must(t, page.AddInitScript(playwright.Script{Content: new("(" + perfObservers + ")()")}))

	var samples []perfSample
	for _, cache := range []string{"cold", "warm"} {
		response := goTo(t, page, path)
		if response == nil || !response.Ok() || currentPath(t, page) != path {
			t.Fatalf("failed measurement navigation: %s -> %s", path, page.URL())
		}
		_, err := page.Evaluate(`() => document.fonts.ready.then(() => true)`)
		must(t, err)
		_, err = page.WaitForFunction(`() => window.perf.lcp > 0`, nil, playwright.PageWaitForFunctionOptions{Timeout: new(15_000.0)})
		must(t, err)
		// Give late layout shifts and long tasks a fixed window to be observed.
		time.Sleep(500 * time.Millisecond)
		raw, err := page.Evaluate(perfCollect)
		must(t, err)
		sample := perfSample{Profile: profile, Path: path, Run: run, Cache: cache}
		must(t, json.Unmarshal([]byte(raw.(string)), &sample))
		samples = append(samples, sample)
	}
	return samples
}

func summarize(samples []perfSample) []perfSummary {
	var keys []string
	groups := map[string][]perfSample{}
	for _, sample := range samples {
		key := fmt.Sprintf("%s %s %s", sample.Profile, sample.Path, sample.Cache)
		if _, seen := groups[key]; !seen {
			keys = append(keys, key)
		}
		groups[key] = append(groups[key], sample)
	}
	summary := make([]perfSummary, 0, len(keys))
	for _, key := range keys {
		rows := groups[key]
		metric := func(value func(perfSample) float64) float64 {
			values := make([]float64, len(rows))
			for i, row := range rows {
				values[i] = value(row)
			}
			slices.Sort(values)
			p75 := values[int(math.Ceil(float64(len(values))*0.75))-1]
			return math.Round(p75*100) / 100
		}
		summary = append(summary, perfSummary{
			Key:              key,
			LCP:              metric(func(s perfSample) float64 { return s.LCP }),
			CLS:              metric(func(s perfSample) float64 { return s.CLS }),
			TTFB:             metric(func(s perfSample) float64 { return s.TTFB }),
			DOMContentLoaded: metric(func(s perfSample) float64 { return s.DOMContentLoaded }),
			Bytes:            metric(func(s perfSample) float64 { return s.Bytes }),
			LongTasks:        metric(func(s perfSample) float64 { return s.LongTasks }),
		})
	}
	return summary
}

func compareBaseline(t *testing.T, path string, summary []perfSummary) {
	t.Helper()
	raw, err := os.ReadFile(path)
	must(t, err)
	var baseline perfReport
	must(t, json.Unmarshal(raw, &baseline))
	for _, row := range summary {
		index := slices.IndexFunc(baseline.Summary, func(old perfSummary) bool { return old.Key == row.Key })
		if index < 0 {
			t.Errorf("missing baseline: %s", row.Key)
			continue
		}
		before := baseline.Summary[index]
		if row.LCP > before.LCP*1.2+100 {
			t.Errorf("%s: lcp %v -> %v", row.Key, before.LCP, row.LCP)
		}
		if row.DOMContentLoaded > before.DOMContentLoaded*1.2+100 {
			t.Errorf("%s: domContentLoaded %v -> %v", row.Key, before.DOMContentLoaded, row.DOMContentLoaded)
		}
		if row.CLS > max(0.1, before.CLS+0.02) {
			t.Errorf("%s: CLS %v", row.Key, row.CLS)
		}
	}
}
