package e2e

import (
	"context"
	"errors"
	"net/http/httptest"
	"os"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/mxschmitt/playwright-go"
	"terrascale.tech/dashboard/internal/app"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/config"
)

// contractMetrics implements the provider contract without a dashboard HTTP
// workaround. Production uses the forthcoming typed ts-go client methods.
type contractMetrics struct {
	mode    string
	mu      sync.Mutex
	scopes  []core.MetricsScope
	hold    atomic.Bool
	started chan struct{}
	release chan struct{}
}

func (c *contractMetrics) Analytics(ctx context.Context, _ string, s core.MetricsScope) (core.Analytics, error) {
	if c.hold.Swap(false) {
		close(c.started)
		select {
		case <-c.release:
		case <-ctx.Done():
			return core.Analytics{}, ctx.Err()
		}
	}
	c.mu.Lock()
	c.scopes = append(c.scopes, s)
	c.mu.Unlock()
	if c.mode == "error" {
		return core.Analytics{}, errors.New("provider offline")
	}
	if c.mode == "empty" {
		return core.Analytics{}, nil
	}
	start := time.Date(2026, 10, 8, 12, 0, 0, 0, time.UTC)
	if c.mode == "sparse" {
		return core.Analytics{Series: []core.AnalyticsPoint{
			{Start: start},
			{Start: start.Add(time.Minute), P95LatencyMS: new(1.5)},
			{Start: start.Add(2 * time.Minute)},
		}}, nil
	}
	if c.mode == "multiple" {
		points := make([]core.AnalyticsPoint, 0, 4)
		for bucket := range 2 {
			for group, region := range []string{"GRU", "SFO"} {
				value := float64(1 + bucket + group)
				points = append(points, core.AnalyticsPoint{Start: start.Add(time.Duration(bucket) * time.Minute), DatabaseID: "01JC9Q8Y5N7P9R3T5V7X9Z2AC", Region: region, P50LatencyMS: new(value), P95LatencyMS: new(value + 3), P99LatencyMS: new(value + 6)})
			}
		}
		return core.Analytics{Series: points}, nil
	}
	return core.Analytics{Series: []core.AnalyticsPoint{
		{Start: start, Requests: map[string]int64{"query": 12, "write": 3}, Operations: map[string]int64{"read": 8, "put": 3}, Errors: 1, P50LatencyMS: new(0.25), P95LatencyMS: new(1.5), P99LatencyMS: new(3.5), StorageBytes: 2048, ClientSentBytes: 1024, ClientReceivedBytes: 2048, ReplicationBytes: 4096},
		{Start: start.Add(time.Minute), Requests: map[string]int64{"query": 20}, Operations: map[string]int64{"read": 12, "delete": 1}, Errors: 2, P50LatencyMS: new(0.5), P95LatencyMS: new(2.5), P99LatencyMS: new(5.0), StorageBytes: 4096, ClientSentBytes: 2048, ClientReceivedBytes: 4096, ReplicationBytes: 2048},
	}}, nil
}

func (c *contractMetrics) Replication(_ context.Context, _ string, s core.MetricsScope) (core.Replication, error) {
	if c.mode == "error" {
		return core.Replication{}, errors.New("provider offline")
	}
	if c.mode == "empty" {
		return core.Replication{}, nil
	}
	id := s.DatabaseID
	if id == "" {
		id = "01JC9Q8Y5N7P9R3T5V7X9Z2AC"
	}
	start := time.Date(2026, 10, 8, 12, 0, 0, 0, time.UTC)
	result := core.Replication{
		Regions: []core.ReplicationRegion{
			{DatabaseID: id, Region: "GRU", Status: "healthy", ReplicatedOps: 12, LagMS: new(float64(4)), LastApplied: new(start)},
			{DatabaseID: id, Region: "SFO", Status: "lagging", ReplicatedOps: 8, LagMS: new(float64(321)), Divergence: new(int64(2)), ConvergenceMS: new(float64(50)), LastApplied: new(start)},
			{DatabaseID: "01JC9Q9Y5N7P9R3T5V7X9Z2AC", Region: "GRU", Status: "diverged", LagMS: new(float64(12)), Divergence: new(int64(5))},
			{DatabaseID: "01JC9Q9Y5N7P9R3T5V7X9Z2AC", Region: "SFO", Status: "stopped"},
		},
		Pairs: []core.ReplicationPair{
			{DatabaseID: id, FromRegion: "gru", ToRegion: "sfo", SentBytes: 4096, ReceivedBytes: 4096, Series: []core.ReplicationPoint{{Start: start, SentBytes: 4096, ReceivedBytes: 4096}}},
			{DatabaseID: id, FromRegion: "sfo", ToRegion: "gru", SentBytes: 2048, ReceivedBytes: 2048, Series: []core.ReplicationPoint{{Start: start.Add(time.Minute), SentBytes: 2048, ReceivedBytes: 2048}}},
		},
	}
	if s.DatabaseID != "" {
		result.Regions = result.Regions[:2]
	}
	return result, nil
}

func (c *contractMetrics) RegionHealth(_ context.Context, codes []string) ([]core.RegionHealth, error) {
	if c.mode == "error" {
		return nil, errors.New("status offline")
	}
	if c.mode == "empty" {
		return nil, nil
	}
	rows := make([]core.RegionHealth, 0, len(codes))
	for _, code := range codes {
		latency := 12.5
		rows = append(rows, core.RegionHealth{Region: code, Status: "healthy", LatencyMS: &latency})
	}
	return rows, nil
}

func metricsPage(t *testing.T, client *contractMetrics) playwright.Page {
	t.Helper()
	page := watchedPage(t, newContext(t, metricsServer(t, client)))
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	return page
}

func metricsServer(t *testing.T, client *contractMetrics) string {
	t.Helper()
	if testing.Short() || os.Getenv("DASHBOARD_E2E_BASE_URL") != "" {
		t.Skip("requires the in-process contract client")
	}
	application, err := app.NewWithMetrics(config.Config{Demo: true, AtlasURL: atlasURL}, client, client)
	must(t, err)
	server := httptest.NewServer(application.Handler)
	t.Cleanup(server.Close)
	return server.URL
}

func TestAnalyticsDatabaseChangesResetStaleRegions(t *testing.T) {
	for _, javascript := range []bool{true, false} {
		name := "with-javascript"
		if !javascript {
			name = "without-javascript"
		}
		t.Run(name, func(t *testing.T) {
			client := &contractMetrics{}
			serverURL := metricsServer(t, client)
			ctx, err := browser.NewContext(playwright.BrowserNewContextOptions{BaseURL: new(serverURL), JavaScriptEnabled: new(javascript)})
			must(t, err)
			t.Cleanup(func() { _ = ctx.Close() })
			page := watchedPage(t, ctx)
			login(t, page, sampleAccount.Email, sampleAccount.Password)
			goToOK(t, page, "/databases")
			click(t, pageByRole(page, *playwright.AriaRoleButton, "New database").First())
			dialog := pageByRole(page, *playwright.AriaRoleDialog, "New database")
			fill(t, dialog.Locator("#database-name"), "analytics-gru")
			selectDatabaseRegions(t, dialog, "GRU")
			submitAndExpect(t, page, "/api/v1/dashboard/databases", 303, func(...playwright.LocatorClickOptions) error {
				return byRole(dialog, *playwright.AriaRoleButton, "Create database").Press("Enter")
			})
			goToOK(t, page, "/analytics?region=SFO&range=1h")
			id := requiredAttribute(t, page.Locator(`#analytics-database option`).Filter(playwright.LocatorFilterOptions{HasText: "analytics-gru"}), "value", "GRU database")
			_, err = page.Locator("#analytics-database").SelectOption(playwright.SelectOptionValues{Values: &[]string{id}})
			must(t, err)
			click(t, pageByRole(page, *playwright.AriaRoleButton, "Apply"))
			must(t, expect.Locator(page.Locator("#analytics-region")).ToHaveValue(""))
			must(t, expect.Locator(page.Locator(`#analytics-region option[value="SFO"]`)).ToHaveCount(0))
			must(t, expect.Locator(page.Locator(`#analytics-region option[value="GRU"]`)).ToHaveCount(1))
			client.mu.Lock()
			last := client.scopes[len(client.scopes)-1]
			client.mu.Unlock()
			if last.DatabaseID != id || last.Region != "" || last.Range != "1h" {
				t.Fatalf("stale region reached provider: %+v", last)
			}
			_, err = page.Locator("#analytics-region").SelectOption(playwright.SelectOptionValues{Values: &[]string{"GRU"}})
			must(t, err)
			click(t, pageByRole(page, *playwright.AriaRoleButton, "Apply"))
			must(t, expect.Locator(page.Locator("#analytics-region")).ToHaveValue("GRU"))
			_, err = page.Locator("#analytics-database").SelectOption(playwright.SelectOptionValues{Values: &[]string{""}})
			must(t, err)
			click(t, pageByRole(page, *playwright.AriaRoleButton, "Apply"))
			must(t, expect.Locator(page.Locator(`#analytics-region option[value="SFO"]`)).ToHaveCount(1))
		})
	}
}

func TestAnalyticsFiltersAndReplicationAcrossDashboard(t *testing.T) {
	client := &contractMetrics{}
	page := metricsPage(t, client)
	goToOK(t, page, "/analytics")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Analytics", true))
	for _, title := range []string{"Requests by type", "Operations by type", "Errors", "Latency percentiles (µs)", "Storage (bytes)", "Client bandwidth (bytes)", "Replication bandwidth (bytes)"} {
		visible(t, pageByRole(page, *playwright.AriaRoleHeading, title))
	}
	must(t, expect.Locator(page.Locator("#analytics-summary")).ToContainText("2.500 ms"))
	must(t, expect.Locator(page.Locator("#analytics-summary")).ToContainText("6.0 KiB"))
	expectAccessible(t, page, "analytics populated", "main")
	options, err := page.Locator("#analytics-database option").All()
	must(t, err)
	if len(options) < 2 {
		t.Fatal("seeded database filters missing")
	}
	dbID := requiredAttribute(t, options[1], "value", "database filter ID")
	for _, rangeKey := range []string{"1h", "24h", "7d", "30d"} {
		_, err = page.Locator("#analytics-range").SelectOption(playwright.SelectOptionValues{Values: &[]string{rangeKey}})
		must(t, err)
		_, err = page.Locator("#analytics-database").SelectOption(playwright.SelectOptionValues{Values: &[]string{dbID}})
		must(t, err)
		_, err = page.Locator("#analytics-region").SelectOption(playwright.SelectOptionValues{Values: &[]string{"SFO"}})
		must(t, err)
		click(t, pageByRole(page, *playwright.AriaRoleButton, "Apply"))
		must(t, expect.Locator(page.Locator("#analytics-range")).ToHaveValue(rangeKey))
		client.mu.Lock()
		last := client.scopes[len(client.scopes)-1]
		client.mu.Unlock()
		if last.Range != rangeKey || last.DatabaseID != dbID || last.Region != "SFO" {
			t.Fatalf("provider scope=%+v", last)
		}
	}
	screenshot(t, page, "analytics-filtered")
	goToOK(t, page, "/?range=1h")
	must(t, expect.Locator(page.Locator("#home-regions")).ToContainText("12.50 ms"))
	must(t, expect.Locator(page.Locator("#home-regions")).ToContainText("healthy"))
	must(t, expect.Locator(page.Locator("#home-replication")).ToContainText("321 ms"))
	must(t, expect.Locator(page.Locator("#home-replication")).ToContainText("6.0 KiB"))
	for _, pair := range []string{"gru → sfo", "sfo → gru"} {
		must(t, expect.Locator(page.Locator("#home-replication")).ToContainText(pair))
	}
	for _, status := range []string{"Healthy", "Lagging", "Diverged", "Stopped"} {
		must(t, expect.Locator(page.Locator("#home-replication")).ToContainText(status))
	}
	expectAccessible(t, page, "overview analytics and regions", "main")
	screenshot(t, page, "overview-analytics-replication-regions")
	click(t, pageByRole(page, *playwright.AriaRoleLink, "View usage replication →"))
	must(t, expect.Locator(page.Locator("#replication")).ToContainText("replication_bytes"))
	must(t, expect.Locator(page.Locator("#replication")).ToContainText("6.0 KiB"))
	expectAccessible(t, page, "usage replication", "#replication")
	screenshot(t, page, "usage-replication")
	goToOK(t, page, "/databases/"+dbID)
	must(t, expect.Locator(page.Locator("#replication")).ToContainText("321 ms"))
	must(t, expect.Locator(page.Locator("#replication")).ToContainText("50 ms"))
	must(t, expect.Locator(page.Locator("#replication")).ToContainText("Oct 8, 2026 12:00:00 UTC"))
	must(t, expect.Locator(page.Locator("main")).Not().ToContainText("replication lag are not available"))
	screenshot(t, page, "database-replication")
}

func TestAnalyticsLoadingStateDuringNativeFilterNavigation(t *testing.T) {
	client := &contractMetrics{started: make(chan struct{}), release: make(chan struct{})}
	page := metricsPage(t, client)
	goToOK(t, page, "/analytics")
	client.hold.Store(true)
	t.Cleanup(func() {
		select {
		case <-client.release:
		default:
			close(client.release)
		}
	})
	loading, err := page.Evaluate(`async () => {
		document.querySelector('#analytics-range').value='7d';
		document.querySelector('form[action="/analytics"]').requestSubmit();
		await new Promise(resolve => requestAnimationFrame(resolve));
		const status=document.querySelector('.ts-measurement-loading');
		return status?.textContent.includes('Loading analytics') && getComputedStyle(status).display !== 'none';
	}`)
	must(t, err)
	select {
	case <-client.started:
	case <-time.After(10 * time.Second):
		t.Fatal("filter never reached the provider")
	}
	if loading != true {
		t.Fatal("native filter navigation did not show the loading status")
	}
	close(client.release)
	must(t, expect.Locator(page.Locator("#analytics-range")).ToHaveValue("7d"))
	must(t, expect.Locator(page.Locator(".ts-measurement-loading")).ToBeHidden())
}

func TestAnalyticsIsolatedLatencyRemainsVisibleWithoutHover(t *testing.T) {
	page := metricsPage(t, &contractMetrics{mode: "sparse"})
	goToOK(t, page, "/analytics")
	marker := page.Locator(".ts-chart-marker")
	must(t, expect.Locator(marker).ToHaveCount(1))
	must(t, marker.ScrollIntoViewIfNeeded())
	must(t, page.Mouse().Move(0, 0))
	must(t, expect.Locator(marker).ToBeVisible())
	must(t, expect.Locator(marker).ToHaveCSS("opacity", "1"))
	must(t, expect.Locator(page.Locator(".ts-chart-column .ts-chart-marker")).ToHaveCount(0))
	expectAccessible(t, page, "analytics isolated latency", "main")
	screenshot(t, page, "analytics-isolated-latency")
}

func TestAnalyticsMultipleRegionLatencySeriesStayDistinct(t *testing.T) {
	page := metricsPage(t, &contractMetrics{mode: "multiple"})
	goToOK(t, page, "/analytics")
	chart := page.Locator(`svg.ts-chart[aria-label^="Latency percentiles"]`)
	must(t, expect.Locator(chart.Locator(".ts-chart-line")).ToHaveCount(6))
	screenshot(t, page, "analytics-multiple-latency")
	styles, err := chart.Evaluate(`chart => [...chart.querySelectorAll('.ts-chart-line')].map(line => {
		const style = getComputedStyle(line);
		return style.stroke + '/' + style.strokeDasharray;
	})`, nil)
	must(t, err)
	seen := make(map[string]bool)
	for _, style := range styles.([]any) {
		key := style.(string)
		if seen[key] {
			t.Fatalf("latency series repeat visual style %q", key)
		}
		seen[key] = true
	}
	matched, err := chart.Evaluate(`chart => {
		const style = element => {
			const css = getComputedStyle(element);
			return css.stroke + '/' + css.strokeDasharray;
		};
		const legends = chart.closest('section').querySelectorAll('.ts-chart-legend line');
		const keys = chart.querySelectorAll('.ts-chart-column:last-of-type .ts-chart-tip-swatch');
		return [...chart.querySelectorAll('.ts-chart-line')].every((line, i) =>
			legends[i] && keys[i] && style(line) === style(legends[i]) && style(line) === style(keys[i]) &&
			getComputedStyle(legends[i].closest('svg')).backgroundColor === 'rgba(0, 0, 0, 0)');
	}`, nil)
	must(t, err)
	if matched != true {
		t.Fatal("latency curve styles do not match their legend and tooltip keys")
	}
	expectAccessible(t, page, "analytics multiple latency series", "main")
}

func TestAnalyticsAndReplicationEmptyAndErrorStates(t *testing.T) {
	for _, mode := range []string{"empty", "error"} {
		t.Run(mode, func(t *testing.T) {
			page := metricsPage(t, &contractMetrics{mode: mode})
			goToOK(t, page, "/analytics")
			message := "No analytics measurements"
			replicationMessage := "No replication measurements"
			regionMessage := "Not reported"
			if mode == "error" {
				message = "Could not load analytics"
				replicationMessage = "Could not load replication"
				regionMessage = "Could not load region health"
			}
			must(t, expect.Locator(page.Locator("#analytics-summary")).ToContainText(message))
			visible(t, pageByRole(page, *playwright.AriaRoleButton, "Apply"))
			goToOK(t, page, "/")
			must(t, expect.Locator(page.Locator("#home-replication")).ToContainText(replicationMessage))
			must(t, expect.Locator(page.Locator("#home-regions")).ToContainText(regionMessage))
			goToOK(t, page, "/usage")
			must(t, expect.Locator(page.Locator("#replication")).ToContainText(replicationMessage))
		})
	}
}
