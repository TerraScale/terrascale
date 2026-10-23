package repllocal

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/replbench"
)

func probePause(ctx context.Context, d time.Duration) bool {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-t.C:
		return ctx.Err() == nil
	}
}

// Keep the original dispatch timestamp across retries so admission stalls remain part of visibility lag.
// Probe PUTs reuse the same ULID and body; retrying an uncertain 503 cannot create another document.
func probeRequest(parent context.Context, region *replbench.Region, method, path string, body []byte, contentType string) (replbench.Response, error) {
	ctx, cancel := context.WithTimeout(parent, 30*time.Second)
	defer cancel()
	for {
		resp, err := region.Do(ctx, method, path, body, contentType)
		if err != nil || resp.Status != http.StatusServiceUnavailable {
			return resp, err
		}
		if !probePause(ctx, 100*time.Millisecond) {
			return resp, ctx.Err()
		}
	}
}

func StartVisibility(ctx context.Context, targets replbench.Targets) (func() (map[string]replbench.Summary, error), error) {
	if _, ok := targets.Regions["gru"]; !ok || len(targets.Regions) < 2 {
		return nil, errors.New("visibility needs gru and another replica")
	}
	regions := make(map[string]*replbench.Region)
	for name := range targets.Regions {
		r, err := replbench.NewRegion(name, targets, len(targets.Regions)+1)
		if err != nil {
			return nil, fmt.Errorf("visibility client %s: invalid target", name)
		}
		regions[name] = r
	}
	collection := "bench-" + strings.ToLower(ulid.Make().String())
	setup, cancel := context.WithTimeout(ctx, 2*time.Minute)
	defer cancel()
	body, _ := json.Marshal(map[string]string{"name": collection})
	resp, err := regions["gru"].Do(setup, http.MethodPost, "/collections", body, "application/json")
	if err != nil || resp.Status/100 != 2 {
		return nil, fmt.Errorf("visibility collection creation failed: HTTP %d", resp.Status)
	}
	for name, r := range regions {
		for {
			resp, err := probeRequest(setup, r, http.MethodGet, "/collections/"+collection, nil, "")
			if err == nil && resp.Status == http.StatusOK {
				break
			}
			if err != nil || resp.Status != http.StatusNotFound || !probePause(setup, 10*time.Millisecond) {
				return nil, fmt.Errorf("visibility collection on %s: HTTP %d, deadline/call failure=%t", name, resp.Status, err != nil || setup.Err() != nil)
			}
		}
	}
	hist := make(map[string]*replbench.Latency)
	for from := range regions {
		for to := range regions {
			if from != to {
				hist[from+"->"+to] = replbench.NewLatency()
			}
		}
	}
	run, stop := context.WithCancel(ctx)
	var wg sync.WaitGroup
	var mu sync.Mutex
	var failures []error
	for _, from := range regions {
		wg.Go(func() {
			tick := time.NewTicker(250 * time.Millisecond)
			defer tick.Stop()
			for run.Err() == nil {
				if err := visibilityProbe(run, from, regions, collection, hist); err != nil {
					mu.Lock()
					failures = append(failures, err)
					mu.Unlock()
					return
				}
				select {
				case <-run.Done():
					return
				case <-tick.C:
				}
			}
		})
	}
	type result struct {
		summaries map[string]replbench.Summary
		err       error
	}
	finish := sync.OnceValue(func() result {
		stop()
		wg.Wait()
		summaries := make(map[string]replbench.Summary)
		for pair, h := range hist {
			summaries[pair] = h.Summary()
			if summaries[pair].Count == 0 {
				failures = append(failures, fmt.Errorf("visibility %s: no successful samples", pair))
			}
		}
		return result{summaries, errors.Join(append(failures, ctx.Err())...)}
	})
	return func() (map[string]replbench.Summary, error) { r := finish(); return r.summaries, r.err }, nil
}

// Dispatch-to-first-matching-GET is an upper bound including the 10 ms poll interval and GET latency, even before ACK.
func visibilityProbe(parent context.Context, from *replbench.Region, regions map[string]*replbench.Region, collection string, hist map[string]*replbench.Latency) error {
	// Stop ends scheduling; the in-flight write and reads retain a finite completion window.
	ctx, cancel := context.WithTimeout(context.WithoutCancel(parent), 2*time.Minute)
	defer cancel()
	id := ulid.Make().String()
	body := []byte(`{"probe":"` + id + `"}`)
	type observation struct {
		pair string
		lag  time.Duration
		err  error
	}
	seen := make(chan observation, len(regions)-1)
	dispatch := make(chan struct{})
	var began time.Time
	for name, to := range regions {
		if name == from.Name {
			continue
		}
		go func() {
			<-dispatch
			pair := from.Name + "->" + name
			for {
				resp, err := probeRequest(ctx, to, http.MethodGet, replbench.DocPath(collection, id), nil, "")
				at := time.Since(began)
				if err == nil && resp.Status == http.StatusOK {
					var got struct{ Document json.RawMessage }
					decodeErr := json.Unmarshal(resp.Body, &got)
					canonical, canonErr := replbench.Canonical(got.Document)
					if decodeErr != nil || canonErr != nil || !bytes.Equal(canonical, body) {
						seen <- observation{pair: pair, err: errors.New("wrong document body")}
					} else {
						seen <- observation{pair: pair, lag: at}
					}
					return
				}
				if err != nil || resp.Status != http.StatusNotFound || !probePause(ctx, 10*time.Millisecond) {
					seen <- observation{pair: pair, err: fmt.Errorf("document missing/read failed: HTTP %d", resp.Status)}
					return
				}
			}
		}()
	}
	began = time.Now()
	close(dispatch)
	resp, err := probeRequest(ctx, from, http.MethodPut, replbench.DocPath(collection, id), body, "application/json")
	written := err == nil && resp.Status/100 == 2
	var failures []error
	if !written {
		failures = append(failures, fmt.Errorf("visibility write %s failed: HTTP %d", from.Name, resp.Status))
		cancel()
	}
	for range len(regions) - 1 {
		o := <-seen
		if o.err != nil {
			failures = append(failures, fmt.Errorf("visibility %s: %w", o.pair, o.err))
		} else if written {
			hist[o.pair].Record(o.lag)
		}
	}
	return errors.Join(failures...)
}
