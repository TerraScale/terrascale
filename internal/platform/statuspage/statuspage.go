// Package statuspage reads the public TerraScale status page summary, a
// Statuspage-compatible /api/v2/summary.json, so the signed-in shell can
// announce unresolved incidents and current or upcoming maintenance.
//
// The summary is fetched server-side and cached in memory. Any failure hides
// the banner rather than the page: renders never wait longer than one fetch
// timeout, and only while nothing has been fetched yet.
package statuspage

import (
	"cmp"
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/url"
	"slices"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/buildinfo"
)

const (
	// fetchTimeout bounds one summary request, including its body.
	fetchTimeout = 2 * time.Second
	// freshFor is how long a fetched summary is served before a refresh.
	freshFor = 30 * time.Second
	// maxStale is how long a summary outlives failed refreshes before its
	// notices are dropped, so an old incident never lingers indefinitely.
	maxStale = 10 * time.Minute
	// upcomingWindow is how far ahead scheduled maintenance is announced.
	upcomingWindow = 24 * time.Hour
	// maxNotices caps the banner so it never pushes the page out of view.
	maxNotices = 3
	// maxMessageRunes bounds the incident update a notice quotes; the full
	// text is on the status page.
	maxMessageRunes = 200
	maxBodyBytes    = 1 << 20
)

// Client serves the status page's active notices from an in-memory cache
// that one background request at a time keeps fresh.
type Client struct {
	url     string
	home    string
	timeout time.Duration
	http    *http.Client
	now     func() time.Time

	mu        sync.Mutex
	summary   summary
	loaded    bool          // a fetch has completed, successfully or not
	checked   time.Time     // when the last fetch completed
	succeeded time.Time     // when the last successful fetch completed
	failing   bool          // the last fetch failed; logged once per outage
	refresh   chan struct{} // closed when the running fetch completes
}

// New returns a client for the summary at summaryURL, an absolute http(s)
// URL. Notices link to the status page's home when they carry no link.
func New(summaryURL string) (*Client, error) {
	u, err := url.Parse(summaryURL)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		return nil, fmt.Errorf("status page summary URL %q must be an absolute http(s) URL", summaryURL)
	}
	return &Client{
		url:     summaryURL,
		home:    (&url.URL{Scheme: u.Scheme, Host: u.Host, Path: "/"}).String(),
		timeout: fetchTimeout,
		http:    &http.Client{Timeout: fetchTimeout},
		now:     time.Now,
	}, nil
}

// Notices returns the incidents and maintenance to announce, most severe
// first. A stale summary is served while one refresh runs in the background;
// before the first fetch completes, callers wait for it until it times out
// or ctx ends. Failures yield no notices.
func (c *Client) Notices(ctx context.Context) []core.StatusNotice {
	c.mu.Lock()
	if c.loaded && c.now().Sub(c.checked) < freshFor {
		defer c.mu.Unlock()
		return c.notices()
	}
	done := c.refresh
	if done == nil {
		done = make(chan struct{})
		c.refresh = done
		go c.update(done)
	}
	if c.loaded {
		defer c.mu.Unlock()
		return c.notices()
	}
	c.mu.Unlock()

	select {
	case <-done:
	case <-ctx.Done():
		return nil
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.notices()
}

// update runs one fetch and records its outcome for every waiting render.
func (c *Client) update(done chan struct{}) {
	defer close(done)
	fetched, err := c.fetch()
	c.mu.Lock()
	defer c.mu.Unlock()
	now := c.now()
	c.loaded, c.checked, c.refresh = true, now, nil
	switch {
	case err == nil:
		c.summary, c.succeeded = fetched, now
		if c.failing {
			slog.Info("status page summary reachable again", "url", c.url)
		}
	case now.Sub(c.succeeded) > maxStale:
		c.summary = summary{}
	}
	if err != nil && !c.failing {
		slog.Warn("status page summary unavailable; hiding status notices", "url", c.url, "error", err)
	}
	c.failing = err != nil
}

// fetch requests the summary detached from any page request, so a closed
// browser tab never cancels the refresh other renders are waiting for.
func (c *Client) fetch() (summary, error) {
	ctx, cancel := context.WithTimeout(context.Background(), c.timeout)
	defer cancel()
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, c.url, nil)
	if err != nil {
		return summary{}, err
	}
	request.Header.Set("Accept", "application/json")
	request.Header.Set("User-Agent", "terrascale-dashboard/"+buildinfo.Version())
	response, err := c.http.Do(request)
	if err != nil {
		return summary{}, err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return summary{}, fmt.Errorf("unexpected HTTP status %s", response.Status)
	}
	return parse(io.LimitReader(response.Body, maxBodyBytes))
}

// summary holds the parts of summary.json the banner uses.
type summary struct {
	Incidents    []incident
	Maintenances []maintenance
}

type incident struct {
	Name      string   `json:"name"`
	Status    string   `json:"status"`
	Impact    string   `json:"impact"`
	Shortlink string   `json:"shortlink"`
	Updates   []update `json:"incident_updates"`
}

type maintenance struct {
	Name           string    `json:"name"`
	Status         string    `json:"status"`
	ScheduledFor   time.Time `json:"scheduled_for"`
	ScheduledUntil time.Time `json:"scheduled_until"`
	Shortlink      string    `json:"shortlink"`
}

// update is one incident update; Statuspage lists the newest first.
type update struct {
	Body string `json:"body"`
}

var errNoSummary = errors.New("response is not a status page summary")

func parse(r io.Reader) (summary, error) {
	var document struct {
		Status struct {
			Indicator string `json:"indicator"`
		} `json:"status"`
		Incidents    []incident    `json:"incidents"`
		Maintenances []maintenance `json:"scheduled_maintenances"`
	}
	if err := json.UnmarshalRead(r, &document); err != nil {
		return summary{}, err
	}
	if document.Status.Indicator == "" {
		return summary{}, errNoSummary
	}
	return summary{Incidents: document.Incidents, Maintenances: document.Maintenances}, nil
}

// notices lists the cached summary's announcements; callers hold c.mu.
func (c *Client) notices() []core.StatusNotice {
	return noticesAt(c.summary, c.now(), c.home)
}

func noticesAt(s summary, now time.Time, home string) []core.StatusNotice {
	type ranked struct {
		rank  int
		start time.Time
		core.StatusNotice
	}
	var list []ranked
	for _, i := range s.Incidents {
		if i.Status == "resolved" || i.Status == "postmortem" || i.Name == "" {
			continue
		}
		impact := impactOf(i.Impact)
		notice := core.StatusNotice{Impact: impact, Label: incidentLabels[impact], Title: i.Name, URL: link(i.Shortlink, home)}
		if len(i.Updates) > 0 {
			notice.Message = headline(i.Updates[0].Body)
		}
		list = append(list, ranked{rank: incidentRanks[impact], StatusNotice: notice})
	}
	for _, m := range s.Maintenances {
		notice := core.StatusNotice{Impact: "maintenance", Title: m.Name, Message: window(m.ScheduledFor, m.ScheduledUntil), URL: link(m.Shortlink, home)}
		switch {
		case m.Name == "":
			continue
		case m.Status == "in_progress":
			notice.Label = "Maintenance in progress"
			list = append(list, ranked{rank: 4, start: m.ScheduledFor, StatusNotice: notice})
		case m.Status == "scheduled" && !m.ScheduledFor.IsZero() && m.ScheduledFor.Sub(now) <= upcomingWindow &&
			(m.ScheduledUntil.IsZero() || m.ScheduledUntil.After(now)):
			notice.Label = "Scheduled maintenance"
			list = append(list, ranked{rank: 5, start: m.ScheduledFor, StatusNotice: notice})
		}
	}
	slices.SortStableFunc(list, func(a, b ranked) int {
		return cmp.Or(cmp.Compare(a.rank, b.rank), a.start.Compare(b.start))
	})
	notices := make([]core.StatusNotice, 0, min(len(list), maxNotices))
	for _, item := range list[:min(len(list), maxNotices)] {
		notices = append(notices, item.StatusNotice)
	}
	return notices
}

var (
	incidentRanks  = map[string]int{"critical": 0, "major": 1, "minor": 2, "none": 3}
	incidentLabels = map[string]string{
		"critical": "Critical incident", "major": "Major incident", "minor": "Minor incident", "none": "Incident",
	}
)

// headline is the first paragraph of an incident update with its whitespace
// collapsed, cut at a word boundary to maxMessageRunes.
func headline(body string) string {
	var words []string
	for line := range strings.Lines(body) {
		fields := strings.Fields(line)
		if len(fields) == 0 && len(words) > 0 {
			break
		}
		words = append(words, fields...)
	}
	text := strings.Join(words, " ")
	if utf8.RuneCountInString(text) <= maxMessageRunes {
		return text
	}
	runes := []rune(text)
	cut := string(runes[:maxMessageRunes])
	if space := strings.LastIndexByte(cut, ' '); space > 0 && runes[maxMessageRunes] != ' ' {
		cut = cut[:space]
	}
	return strings.TrimRight(cut, " ,.;:") + "…"
}

// impactOf maps an incident impact onto the banner's finite variants.
func impactOf(impact string) string {
	if _, ok := incidentRanks[impact]; ok {
		return impact
	}
	return "none"
}

// link returns shortlink when it is an absolute http(s) URL, else home.
func link(shortlink, home string) string {
	u, err := url.Parse(shortlink)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		return home
	}
	return shortlink
}

// window describes a maintenance window in UTC, e.g. "Sep 27, 14:00–16:00 UTC".
func window(from, until time.Time) string {
	const day, clock = "Jan 2, 15:04", "15:04"
	from, until = from.UTC(), until.UTC()
	switch {
	case from.IsZero():
		return ""
	case until.IsZero():
		return "From " + from.Format(day) + " UTC"
	case from.YearDay() == until.YearDay() && from.Year() == until.Year():
		return from.Format(day) + "–" + until.Format(clock) + " UTC"
	default:
		return from.Format(day) + " – " + until.Format(day) + " UTC"
	}
}
