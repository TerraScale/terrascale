package replbench

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// FeedEvent is one change event of a database's changes feed (GET …/changes): Version is the writing operation's HLC
// stamp and Origin the node that ran it (16 hex digits), the pair a document's last-writer-wins order compares.
type FeedEvent struct {
	Collection string `json:"collection"`
	ID         string `json:"id"`
	Op         string `json:"op"`
	Version    string `json:"version"`
	Origin     string `json:"origin"`
}

// Stamp is a feed event's (HLC, node) version as numbers.
type Stamp struct {
	HLC  uint64
	Node uint64
}

// ParseStamp reads an event's decimal version and hexadecimal origin.
func ParseStamp(e FeedEvent) (Stamp, error) {
	hlc, err := strconv.ParseUint(e.Version, 10, 64)
	if err != nil {
		return Stamp{}, fmt.Errorf("version %q: %w", e.Version, err)
	}
	node, err := strconv.ParseUint(e.Origin, 16, 64)
	if err != nil {
		return Stamp{}, fmt.Errorf("origin %q: %w", e.Origin, err)
	}
	return Stamp{hlc, node}, nil
}

// Newer is TerraBase's EntryVersion order: the stamp first, the node breaks ties (src/TerraBase/Engine/Lattices/EntryVersion.cs).
func (s Stamp) Newer(o Stamp) bool {
	if s.HLC != o.HLC {
		return s.HLC > o.HLC
	}
	return s.Node > o.Node
}

// LWWWinner is the origin whose write must survive among one document's competing writes, keyed by origin.
func LWWWinner(writes map[string]Stamp) string {
	winner := ""
	for origin, s := range writes {
		if winner == "" || s.Newer(writes[winner]) {
			winner = origin
		}
	}
	return winner
}

// ErrFeedIdle ends a ReadFeed that saw no event for its idle period before seen was satisfied.
var ErrFeedIdle = errors.New("no change event within the idle period")

// ReadFeed streams region's changes feed for collection from the start of retention and hands every change event to
// seen until seen answers true (got everything it wants) or idle passes without an event.
func ReadFeed(ctx context.Context, r *Region, collection string, idle time.Duration, seen func(FeedEvent) bool) error {
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	u := *r.base
	u.Path = r.dbPath + "/changes"
	u.RawQuery = url.Values{"collections": {collection}}.Encode()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u.String(), nil)
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+r.token)
	req.Header.Set("Accept", "text/event-stream")
	// The stream stays open; the client's whole-request timeout would cut it, so this request uses its own client.
	resp, err := (&http.Client{Transport: r.client.Transport}).Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("changes on %s: HTTP %d", r.Name, resp.StatusCode)
	}
	lines := make(chan string)
	go func() {
		defer close(lines)
		sc := bufio.NewScanner(resp.Body)
		sc.Buffer(make([]byte, 64<<10), 4<<20)
		for sc.Scan() {
			select {
			case lines <- sc.Text():
			case <-ctx.Done():
				return
			}
		}
	}()
	event := ""
	timer := time.NewTimer(idle)
	defer timer.Stop()
	for {
		select {
		case <-timer.C:
			return fmt.Errorf("changes on %s: %w", r.Name, ErrFeedIdle)
		case line, ok := <-lines:
			if !ok {
				return fmt.Errorf("changes on %s: stream ended", r.Name)
			}
			switch {
			case strings.HasPrefix(line, "event: "):
				event = strings.TrimPrefix(line, "event: ")
			case strings.HasPrefix(line, "data: ") && event == "change":
				var e FeedEvent
				if err := json.Unmarshal([]byte(strings.TrimPrefix(line, "data: ")), &e); err != nil {
					return err
				}
				timer.Reset(idle)
				if seen(e) {
					return nil
				}
			}
		}
	}
}
