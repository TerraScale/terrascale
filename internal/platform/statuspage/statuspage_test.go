package statuspage

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// sampleSummary is a Statuspage-compatible summary with one resolved and
// two unresolved incidents, and maintenance in progress, soon, and later.
const sampleSummary = `{
  "page": {"id": "terrascale", "name": "TerraScale", "url": "https://status.terrascale.tech"},
  "status": {"indicator": "major", "description": "Partial System Outage"},
  "components": [{"id": "dash", "name": "Dashboard", "status": "partial_outage"}],
  "incidents": [
    {"name": "Slow sign-in", "status": "monitoring", "impact": "minor",
     "shortlink": "https://status.terrascale.tech/incidents/1",
     "incident_updates": [{"body": "A fix is rolling out."}, {"body": "Investigating slow sign-in."}]},
    {"name": "Old outage", "status": "resolved", "impact": "critical",
     "shortlink": "https://status.terrascale.tech/incidents/0", "incident_updates": []},
    {"name": "Database API errors", "status": "investigating", "impact": "major",
     "shortlink": "https://status.terrascale.tech/incidents/2",
     "incident_updates": [{"body": "We are investigating elevated error rates."}]}
  ],
  "scheduled_maintenances": [
    {"name": "Network upgrade", "status": "in_progress",
     "scheduled_for": "2026-09-26T11:00:00Z", "scheduled_until": "2026-09-26T13:00:00Z",
     "shortlink": "https://status.terrascale.tech/incidents/3"},
    {"name": "Storage migration", "status": "scheduled",
     "scheduled_for": "2026-09-27T02:00:00Z", "scheduled_until": "2026-09-27T04:00:00Z",
     "shortlink": "https://status.terrascale.tech/incidents/4"},
    {"name": "Next month's upgrade", "status": "scheduled",
     "scheduled_for": "2026-10-20T02:00:00Z", "scheduled_until": null,
     "shortlink": "https://status.terrascale.tech/incidents/5"}
  ]
}`

const home = "https://status.terrascale.tech/"

var sampleNow = time.Date(2026, 9, 26, 12, 0, 0, 0, time.UTC)

func TestParseReadsIncidentsAndMaintenance(t *testing.T) {
	s, err := parse(strings.NewReader(sampleSummary))
	if err != nil {
		t.Fatal(err)
	}
	if len(s.Incidents) != 3 || len(s.Maintenances) != 3 {
		t.Fatalf("parsed %d incidents and %d maintenances, want 3 and 3", len(s.Incidents), len(s.Maintenances))
	}
	if got := s.Incidents[0]; got.Name != "Slow sign-in" || got.Impact != "minor" || got.Status != "monitoring" ||
		got.Shortlink != "https://status.terrascale.tech/incidents/1" || got.Headline != "A fix is rolling out." || got.Updates != nil {
		t.Fatalf("incident = %+v", got)
	}
	if got := s.Maintenances[1]; got.Status != "scheduled" ||
		!got.ScheduledFor.Equal(time.Date(2026, 9, 27, 2, 0, 0, 0, time.UTC)) ||
		!got.ScheduledUntil.Equal(time.Date(2026, 9, 27, 4, 0, 0, 0, time.UTC)) {
		t.Fatalf("maintenance = %+v", got)
	}
	if !s.Maintenances[2].ScheduledUntil.IsZero() {
		t.Fatalf("null scheduled_until = %v, want zero", s.Maintenances[2].ScheduledUntil)
	}
}

func TestParseRejectsDocumentsThatAreNotSummaries(t *testing.T) {
	for name, body := range map[string]string{
		"empty object":   `{}`,
		"no indicator":   `{"status": {"description": "All Systems Operational"}, "incidents": []}`,
		"html":           `<!doctype html><title>Status</title>`,
		"truncated":      `{"status": {"indicator": "none"}, "incidents": [`,
		"wrong type":     `{"status": {"indicator": "none"}, "incidents": {}}`,
		"invalid window": `{"status": {"indicator": "maintenance"}, "scheduled_maintenances": [{"scheduled_for": "soon"}]}`,
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := parse(strings.NewReader(body)); err == nil {
				t.Fatal("parse accepted a document that is not a summary")
			}
		})
	}
}

func TestNoticesAnnounceUnresolvedIncidentsAndCurrentOrUpcomingMaintenance(t *testing.T) {
	s, err := parse(strings.NewReader(sampleSummary))
	if err != nil {
		t.Fatal(err)
	}
	got := noticesAt(summary{Incidents: s.Incidents, Maintenances: s.Maintenances[1:]}, sampleNow, home)
	want := []core.StatusNotice{
		{Impact: "major", Label: "Major incident", Title: "Database API errors",
			Message: "We are investigating elevated error rates.", URL: "https://status.terrascale.tech/incidents/2"},
		{Impact: "minor", Label: "Minor incident", Title: "Slow sign-in",
			Message: "A fix is rolling out.", URL: "https://status.terrascale.tech/incidents/1"},
		{Impact: "maintenance", Label: "Scheduled maintenance", Title: "Storage migration",
			Message: "Sep 27, 02:00–04:00 UTC", URL: "https://status.terrascale.tech/incidents/4"},
	}
	assertNotices(t, got, want)

	// In-progress maintenance outranks scheduled maintenance, and the banner
	// keeps at most three rows.
	got = noticesAt(s, sampleNow, home)
	if len(got) != maxNotices || got[2].Title != "Network upgrade" || got[2].Label != "Maintenance in progress" ||
		got[2].Message != "Sep 26, 11:00–13:00 UTC" {
		t.Fatalf("notices = %+v", got)
	}
}

func TestNoticesSkipMaintenanceOutsideTheWindowAndSanitizeLinks(t *testing.T) {
	for _, tc := range []struct {
		name string
		m    maintenance
		want []core.StatusNotice
	}{
		{"more than a day ahead", maintenance{Name: "Later", Status: "scheduled", ScheduledFor: sampleNow.Add(25 * time.Hour)}, nil},
		{"already over", maintenance{Name: "Over", Status: "scheduled", ScheduledFor: sampleNow.Add(-2 * time.Hour), ScheduledUntil: sampleNow.Add(-time.Hour)}, nil},
		{"completed", maintenance{Name: "Done", Status: "completed", ScheduledFor: sampleNow.Add(-2 * time.Hour)}, nil},
		{"no start", maintenance{Name: "Unplanned", Status: "scheduled"}, nil},
		{"open ended, unsafe link", maintenance{Name: "Soon", Status: "scheduled", ScheduledFor: sampleNow.Add(time.Hour), Shortlink: "javascript:alert(1)"},
			[]core.StatusNotice{{Impact: "maintenance", Label: "Scheduled maintenance", Title: "Soon", Message: "From Sep 26, 13:00 UTC", URL: home}}},
		{"spans days", maintenance{Name: "Overnight", Status: "in_progress", ScheduledFor: sampleNow.Add(-time.Hour), ScheduledUntil: sampleNow.Add(14 * time.Hour), Shortlink: "/relative"},
			[]core.StatusNotice{{Impact: "maintenance", Label: "Maintenance in progress", Title: "Overnight", Message: "Sep 26, 11:00 – Sep 27, 02:00 UTC", URL: home}}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			assertNotices(t, noticesAt(summary{Maintenances: []maintenance{tc.m}}, sampleNow, home), tc.want)
		})
	}
	unknown := noticesAt(summary{Incidents: []incident{{Name: "Odd", Status: "identified", Impact: "catastrophic"}}}, sampleNow, home)
	assertNotices(t, unknown, []core.StatusNotice{{Impact: "none", Label: "Incident", Title: "Odd", URL: home}})
}

func TestNoticesQuoteTheFirstParagraphOfLongUpdates(t *testing.T) {
	sentence := "We are investigating elevated latency for queries routed through the São Paulo hub. "
	long := strings.Repeat(sentence, 4)
	for _, tc := range []struct{ name, body, want string }{
		{"short", "A fix is rolling out.", "A fix is rolling out."},
		{"whitespace", "  Writes are\n\tunaffected.  ", "Writes are unaffected."},
		{"first paragraph", "\n\nIdentified the cause.\r\nRolling back.\r\n\r\nDetails follow: 1, 2, 3.", "Identified the cause. Rolling back."},
		{"word boundary", long, strings.Repeat(sentence, 2) + "We are investigating elevated…"},
		{"exact word end", strings.Repeat("a", maxMessageRunes) + " tail", strings.Repeat("a", maxMessageRunes) + "…"},
		{"one long word", strings.Repeat("é", maxMessageRunes+10), strings.Repeat("é", maxMessageRunes) + "…"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got := headline(tc.body)
			if got != tc.want {
				t.Fatalf("headline = %q, want %q", got, tc.want)
			}
			if n := len([]rune(got)); n > maxMessageRunes+1 {
				t.Fatalf("headline has %d runes, want at most %d", n, maxMessageRunes+1)
			}
		})
	}
}

func TestClientCachesTheSummaryForThirtySeconds(t *testing.T) {
	server := newSummaryServer(t)
	client, clock := newTestClient(t, server.URL)

	for range 3 {
		assertTitles(t, client.Notices(t.Context()), "Database API errors", "Slow sign-in", "Network upgrade")
	}
	if got := server.hits.Load(); got != 1 {
		t.Fatalf("fresh summary fetched %d times, want 1", got)
	}

	// Once stale, the cached notices are served at once while one refresh
	// runs in the background.
	clock.advance(freshFor + time.Second)
	server.set(http.StatusOK, `{"status": {"indicator": "none"}, "incidents": [], "scheduled_maintenances": []}`)
	assertTitles(t, client.Notices(t.Context()), "Database API errors", "Slow sign-in", "Network upgrade")
	waitForRefresh(client)
	if got := server.hits.Load(); got != 2 {
		t.Fatalf("stale summary fetched %d times, want 2", got)
	}
	assertTitles(t, client.Notices(t.Context()))
}

func TestClientFetchesOnceForConcurrentRenders(t *testing.T) {
	server := newSummaryServer(t)
	release := make(chan struct{})
	server.gate = release
	client, _ := newTestClient(t, server.URL)

	var wg sync.WaitGroup
	results := make([][]core.StatusNotice, 16)
	for i := range results {
		wg.Go(func() { results[i] = client.Notices(t.Context()) })
	}
	for server.hits.Load() == 0 {
		time.Sleep(time.Millisecond)
	}
	close(release)
	wg.Wait()
	if got := server.hits.Load(); got != 1 {
		t.Fatalf("concurrent renders fetched %d times, want 1", got)
	}
	for _, notices := range results {
		assertTitles(t, notices, "Database API errors", "Slow sign-in", "Network upgrade")
	}
}

func TestClientShowsNothingWhenTheStatusPageFails(t *testing.T) {
	for _, tc := range []struct {
		name   string
		status int
		body   string
	}{
		{"server error", http.StatusServiceUnavailable, sampleSummary},
		{"not found", http.StatusNotFound, `{"error": "not found"}`},
		{"not a summary", http.StatusOK, `<!doctype html><p>Status</p>`},
	} {
		t.Run(tc.name, func(t *testing.T) {
			server := newSummaryServer(t)
			server.set(tc.status, tc.body)
			client, _ := newTestClient(t, server.URL)
			assertTitles(t, client.Notices(t.Context()))
		})
	}

	t.Run("unreachable", func(t *testing.T) {
		server := httptest.NewServer(http.NotFoundHandler())
		server.Close()
		client, _ := newTestClient(t, server.URL)
		assertTitles(t, client.Notices(t.Context()))
	})

	t.Run("slower than the timeout", func(t *testing.T) {
		server := newSummaryServer(t)
		release := make(chan struct{})
		t.Cleanup(func() { close(release) })
		server.gate = release
		client, _ := newTestClient(t, server.URL)
		client.timeout = 50 * time.Millisecond
		started := time.Now()
		assertTitles(t, client.Notices(t.Context()))
		if elapsed := time.Since(started); elapsed > time.Second {
			t.Fatalf("render waited %v for a timed-out fetch", elapsed)
		}
	})

	t.Run("render gives up with its request", func(t *testing.T) {
		server := newSummaryServer(t)
		release := make(chan struct{})
		t.Cleanup(func() { close(release) })
		server.gate = release
		client, _ := newTestClient(t, server.URL)
		ctx, cancel := context.WithCancel(t.Context())
		cancel()
		assertTitles(t, client.Notices(ctx))
	})
}

func TestClientServesStaleNoticesWhileTheStatusPageFails(t *testing.T) {
	server := newSummaryServer(t)
	client, clock := newTestClient(t, server.URL)
	assertTitles(t, client.Notices(t.Context()), "Database API errors", "Slow sign-in", "Network upgrade")

	server.set(http.StatusBadGateway, "")
	clock.advance(freshFor + time.Second)
	client.Notices(t.Context())
	waitForRefresh(client)
	assertTitles(t, client.Notices(t.Context()), "Database API errors", "Slow sign-in", "Network upgrade")
	if got := server.hits.Load(); got != 2 {
		t.Fatalf("failed refresh retried before the cache expired: %d fetches, want 2", got)
	}

	// A summary that has not refreshed for maxStale is dropped.
	clock.advance(maxStale)
	client.Notices(t.Context())
	waitForRefresh(client)
	assertTitles(t, client.Notices(t.Context()))
}

func TestNewRejectsRelativeOrNonHTTPURLs(t *testing.T) {
	for _, raw := range []string{"", "/api/v1/summary.json", "ftp://status.terrascale.tech/summary.json", "https://"} {
		if _, err := New(raw); err == nil {
			t.Errorf("New(%q) accepted an invalid summary URL", raw)
		}
	}
	client, err := New("https://status.terrascale.tech/api/v1/summary.json")
	if err != nil || client.home != home {
		t.Fatalf("New() = %+v, %v; want home %q", client, err, home)
	}
}

// summaryServer serves a configurable summary and counts requests. A
// non-nil gate holds every response until it is closed.
type summaryServer struct {
	*httptest.Server
	hits atomic.Int32
	gate chan struct{}

	mu     sync.Mutex
	status int
	body   string
}

func newSummaryServer(t *testing.T) *summaryServer {
	t.Helper()
	s := &summaryServer{status: http.StatusOK, body: sampleSummary}
	s.Server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		s.hits.Add(1)
		if r.URL.Path != "/api/v1/summary.json" {
			http.NotFound(w, r)
			return
		}
		if s.gate != nil {
			select {
			case <-s.gate:
			case <-r.Context().Done():
				return
			}
		}
		s.mu.Lock()
		status, body := s.status, s.body
		s.mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)
		_, _ = w.Write([]byte(body))
	}))
	t.Cleanup(s.Close)
	return s
}

func (s *summaryServer) set(status int, body string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.status, s.body = status, body
}

type fakeClock struct {
	mu  sync.Mutex
	now time.Time
}

func (c *fakeClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.now
}

func (c *fakeClock) advance(d time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.now = c.now.Add(d)
}

func newTestClient(t *testing.T, serverURL string) (*Client, *fakeClock) {
	t.Helper()
	client, err := New(serverURL + "/api/v1/summary.json")
	if err != nil {
		t.Fatal(err)
	}
	clock := &fakeClock{now: sampleNow}
	client.now = clock.Now
	return client, clock
}

// waitForRefresh blocks until the client's background fetch, if any, ends.
func waitForRefresh(c *Client) {
	c.mu.Lock()
	done := c.refresh
	c.mu.Unlock()
	if done != nil {
		<-done
	}
}

func assertTitles(t *testing.T, notices []core.StatusNotice, titles ...string) {
	t.Helper()
	got := make([]string, len(notices))
	for i, notice := range notices {
		got[i] = notice.Title
	}
	if strings.Join(got, "|") != strings.Join(titles, "|") {
		t.Fatalf("notice titles = %q, want %q", got, titles)
	}
}

func assertNotices(t *testing.T, got, want []core.StatusNotice) {
	t.Helper()
	if len(got) != len(want) {
		t.Fatalf("notices = %+v, want %+v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("notice %d = %+v, want %+v", i, got[i], want[i])
		}
	}
}

func TestNoticeIdentitySurvivesUpdatesAndSeparatesNewIncidents(t *testing.T) {
	read := func(body string) []core.StatusNotice {
		t.Helper()
		s, err := parse(strings.NewReader(body))
		if err != nil {
			t.Fatal(err)
		}
		return noticesAt(s, sampleNow, home)
	}
	body := `{"status":{"indicator":"major"},"incidents":[
		{"id":"same-id","name":"Outage","status":"investigating","impact":"major","incident_updates":[{"body":"Investigating"}]}],
		"scheduled_maintenances":[{"id":"same-id","name":"Maintenance","status":"in_progress"}]}`
	first := read(body)
	if len(first) != 2 || first[0].ID != "incident:same-id" || first[1].ID != "maintenance:same-id" {
		t.Fatalf("notice identities = %+v", first)
	}
	updated := read(strings.NewReplacer("Outage", "Renamed outage", "Investigating", "Fix applied", "investigating", "monitoring").Replace(body))
	if updated[0].ID != first[0].ID || updated[0].Title == first[0].Title || updated[0].Message == first[0].Message {
		t.Fatalf("updating an incident changed its identity or lost its update: %+v", updated)
	}
	newIncident := read(strings.Replace(body, `"id":"same-id"`, `"id":"new-id"`, 1))
	if newIncident[0].ID == first[0].ID {
		t.Fatal("a new incident inherited the old incident's identity")
	}
	resolved := read(strings.Replace(body, `"status":"investigating"`, `"status":"resolved"`, 1))
	if len(resolved) != 1 || resolved[0].ID != first[1].ID {
		t.Fatalf("resolved incident is still announced: %+v", resolved)
	}
}
