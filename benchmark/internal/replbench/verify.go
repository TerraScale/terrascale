package replbench

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"slices"
	"sync"
	"time"

	"github.com/oklog/ulid/v2"
)

// RegionCheck is one region's listing compared with what the load wrote.
type RegionCheck struct {
	Count      int      `json:"count"`
	Digest     string   `json:"digest"`
	Missing    int      `json:"missing"`
	Extra      int      `json:"extra"`
	Mismatched int      `json:"mismatched"`
	Examples   []string `json:"examples,omitempty"`
	ListSecs   float64  `json:"list_seconds"`
}

func (c RegionCheck) Exact() bool { return c.Missing == 0 && c.Extra == 0 && c.Mismatched == 0 }

// Convergence is how long every region took to hold every write after the load's last acknowledgement.
type Convergence struct {
	MarkersSecs   float64                `json:"markers_seconds"`     // every region's end marker visible in every region
	VerifiedSecs  float64                `json:"verified_seconds"`    // start of the first listing round that matched everywhere
	VerifiedBySec float64                `json:"verified_by_seconds"` // end of that round: an upper bound
	Rounds        int                    `json:"rounds"`
	Expected      int                    `json:"expected"`
	ExpectedHash  string                 `json:"expected_digest"`
	Deleted       int                    `json:"deleted"`
	Unknown       int                    `json:"unknown"`
	Regions       map[string]RegionCheck `json:"regions"`
	Converged     bool                   `json:"converged"`
}

// Converge writes an end marker in every region, waits until every region serves all of them, then lists every region
// in rounds until each holds exactly the expected documents or Config.Converge runs out.
func (r *Runner) Converge(ctx context.Context, loadEnd time.Time) (Convergence, error) {
	out := Convergence{Expected: len(r.expected), Deleted: r.deleted, Unknown: len(r.unknown), ExpectedHash: digest(r.expected)}
	coll := r.collection + "-probe"
	var markers []string
	for _, reg := range r.regions {
		id := ulid.Make().String() // the document API takes ULID ids only (#408)
		if resp, ok, _ := r.call(ctx, reg, http.MethodPut, DocPath(coll, id), []byte(`{"end":true}`), "application/json"); !ok {
			return out, fmt.Errorf("end marker on %s: HTTP %d", reg.Name, resp.Status)
		}
		markers = append(markers, id)
	}
	deadline := loadEnd.Add(r.cfg.Converge)
	err := until(ctx, time.Until(deadline), func() bool {
		for _, reg := range r.regions {
			for _, id := range markers {
				if resp, err := reg.Do(ctx, http.MethodGet, DocPath(coll, id), nil, ""); err != nil || resp.Status != http.StatusOK {
					return false
				}
			}
		}
		return true
	})
	if err != nil {
		return out, fmt.Errorf("end markers: %w", err)
	}
	out.MarkersSecs = time.Since(loadEnd).Seconds()
	r.log("end markers everywhere after %.1f s; listing every region", out.MarkersSecs)
	for time.Now().Before(deadline) {
		round := time.Now()
		out.Rounds++
		checks, err := r.checkAll(ctx)
		if err != nil {
			return out, err
		}
		out.Regions = checks
		// Every region matches the load, and the regions match each other (writes of unknown outcome included).
		exact := true
		for name, c := range checks {
			exact = exact && c.Exact() && c.Digest == checks[r.regions[0].Name].Digest
			r.log("round %d %s: %d documents, missing %d, extra %d, mismatched %d (%.1f s)", out.Rounds, name, c.Count, c.Missing, c.Extra, c.Mismatched, c.ListSecs)
		}
		if exact {
			out.Converged = true
			out.VerifiedSecs = round.Sub(loadEnd).Seconds()
			out.VerifiedBySec = time.Since(loadEnd).Seconds()
			return out, nil
		}
		sleep(ctx, 2*time.Second)
	}
	return out, nil
}

func (r *Runner) checkAll(ctx context.Context) (map[string]RegionCheck, error) {
	checks := map[string]RegionCheck{}
	var mu sync.Mutex
	var wg sync.WaitGroup
	var first error
	for _, reg := range r.regions {
		wg.Go(func() {
			c, err := r.check(ctx, reg)
			mu.Lock()
			defer mu.Unlock()
			if err != nil && first == nil {
				first = err
			}
			checks[reg.Name] = c
		})
	}
	wg.Wait()
	return checks, first
}

// check lists the whole collection of one region and compares it document by document with the expected hashes.
func (r *Runner) check(ctx context.Context, reg *Region) (RegionCheck, error) {
	began := time.Now()
	held := make(map[string]Hash, len(r.expected))
	cursor := ""
	for {
		path := "/collections/" + r.collection + "/documents?limit=1000"
		if cursor != "" {
			path += "&cursor=" + url.QueryEscape(cursor)
		}
		resp, err := reg.Do(ctx, http.MethodGet, path, nil, "")
		for attempt := 0; (err != nil || resp.Status/100 == 5 || resp.Status == http.StatusTooManyRequests) && attempt < 6; attempt++ {
			sleep(ctx, time.Duration(100<<attempt)*time.Millisecond)
			resp, err = reg.Do(ctx, http.MethodGet, path, nil, "")
		}
		if err != nil || resp.Status != http.StatusOK {
			return RegionCheck{}, fmt.Errorf("list %s: HTTP %d %v %s", reg.Name, resp.Status, err, resp.Body)
		}
		var page struct {
			Items []struct {
				ID       string          `json:"id"`
				Document json.RawMessage `json:"document"`
			} `json:"items"`
			Next *string `json:"next_cursor"`
		}
		if err := json.Unmarshal(resp.Body, &page); err != nil {
			return RegionCheck{}, fmt.Errorf("list %s: %w", reg.Name, err)
		}
		for _, it := range page.Items {
			c, err := Canonical(it.Document)
			if err != nil {
				return RegionCheck{}, fmt.Errorf("list %s: document %s: %w", reg.Name, it.ID, err)
			}
			held[it.ID] = DocHash(it.ID, c)
		}
		if page.Next == nil || *page.Next == "" {
			break
		}
		cursor = *page.Next
	}
	out := RegionCheck{Count: len(held), Digest: digest(held), ListSecs: time.Since(began).Seconds()}
	example := func(kind, id string) {
		if len(out.Examples) < 10 {
			out.Examples = append(out.Examples, kind+" "+id)
		}
	}
	for id, h := range r.expected {
		switch got, ok := held[id]; {
		case !ok:
			out.Missing++
			example("missing", id)
		case got != h:
			out.Mismatched++
			example("mismatched", id)
		}
	}
	for id := range held {
		if _, ok := r.expected[id]; !ok && !r.unknown[id] {
			out.Extra++
			example("extra", id)
		}
	}
	return out, nil
}

// digest is SHA-256 over "id, tab, hex hash, newline" in id order: equal sets of documents give equal digests.
func digest(docs map[string]Hash) string {
	ids := make([]string, 0, len(docs))
	for id := range docs {
		ids = append(ids, id)
	}
	slices.Sort(ids)
	h := sha256.New()
	var line bytes.Buffer
	for _, id := range ids {
		line.Reset()
		hash := docs[id]
		line.WriteString(id)
		line.WriteByte('\t')
		line.WriteString(hex.EncodeToString(hash[:]))
		line.WriteByte('\n')
		h.Write(line.Bytes())
	}
	return hex.EncodeToString(h.Sum(nil))
}
