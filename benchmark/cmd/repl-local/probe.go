package main

import (
	"bufio"
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strconv"
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

func startVisibility(ctx context.Context, targets replbench.Targets) (func() (map[string]replbench.Summary, error), error) {
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
			resp, err := r.Do(setup, http.MethodGet, "/collections/"+collection, nil, "")
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
				resp, err := to.Do(ctx, http.MethodGet, replbench.DocPath(collection, id), nil, "")
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
	resp, err := from.Do(ctx, http.MethodPut, replbench.DocPath(collection, id), body, "application/json")
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

func openFeeds(ctx context.Context, targets replbench.Targets, n int) (stop func() error, admitted int, rejected int, err error) {
	target, ok := targets.Regions["gru"]
	if n < 0 || !ok {
		return nil, 0, 0, errors.New("feeds need gru and a nonnegative count")
	}
	transport := &http.Transport{DisableKeepAlives: true, ResponseHeaderTimeout: 30 * time.Second}
	if target.CAPEM != "" {
		pool := x509.NewCertPool()
		if !pool.AppendCertsFromPEM([]byte(target.CAPEM)) {
			return nil, 0, 0, errors.New("feed target has invalid CA")
		}
		transport.TLSClientConfig = &tls.Config{RootCAs: pool, MinVersion: tls.VersionTLS12}
	}
	client := &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	run, cancel := context.WithCancel(ctx)
	deadline := time.AfterFunc(30*time.Second, cancel)
	var wg sync.WaitGroup
	var mu sync.Mutex
	var failures []error
	fail := func(err error) { mu.Lock(); failures = append(failures, err); mu.Unlock() }
	stop = sync.OnceValue(func() error {
		deadline.Stop()
		cancel()
		wg.Wait()
		transport.CloseIdleConnections()
		return errors.Join(append(failures, ctx.Err())...)
	})
	endpoint := strings.TrimSuffix(target.URL, "/") + "/v1/projects/" + targets.Project + "/databases/" + targets.Database + "/changes?cursor=now"
	for range n {
		outcome := make(chan int, 1)
		wg.Go(func() {
			reported := false
			defer func() {
				if !reported {
					outcome <- 0
				}
			}()
			for run.Err() == nil {
				req, err := http.NewRequestWithContext(run, http.MethodGet, endpoint, nil)
				if err != nil {
					fail(errors.New("invalid feed URL"))
					return
				}
				req.Header.Set("Authorization", "Bearer "+target.Token)
				req.Header.Set("Accept", "text/event-stream")
				if target.Pin {
					req.Header.Set("fly-prefer-region", "gru")
				}
				resp, err := client.Do(req)
				if err != nil {
					fail(errors.New("feed request failed"))
					return
				}
				if resp.StatusCode != http.StatusOK {
					var refusal struct{ Error struct{ Code string } }
					decodeErr := json.NewDecoder(io.LimitReader(resp.Body, 4096)).Decode(&refusal)
					resp.Body.Close()
					if decodeErr == nil && resp.StatusCode == http.StatusTooManyRequests {
						switch refusal.Error.Code {
						case "changes_busy": // The real 32-stream cap is an expected rejection.
							reported = true
							outcome <- -1
							return
						case "tenant_busy":
							seconds, _ := strconv.Atoi(resp.Header.Get("Retry-After"))
							if probePause(run, max(200*time.Millisecond, time.Duration(seconds)*time.Second)) {
								continue
							}
							return
						}
					}
					reason := "unknown refusal"
					switch refusal.Error.Code {
					case "tenant_memory", "storage_busy", "cursor_expired", "cursor_elsewhere", "invalid_cursor", "not_found", "unauthorized", "forbidden":
						reason = refusal.Error.Code
					}
					fail(fmt.Errorf("unexpected feed refusal: HTTP %d %s", resp.StatusCode, reason))
					return
				}
				defer resp.Body.Close()
				if target.Pin && resp.Header.Get("fly-region") != "gru" || !strings.HasPrefix(resp.Header.Get("Content-Type"), "text/event-stream") {
					fail(errors.New("feed answered from wrong region or with wrong content type"))
					return
				}
				scanner := bufio.NewScanner(resp.Body)
				scanner.Buffer(make([]byte, 4096), 64<<10)
				event := ""
				hasData := false
				for scanner.Scan() {
					line := scanner.Text()
					if value, ok := strings.CutPrefix(line, "event:"); ok {
						event = strings.TrimSpace(value)
					}
					if strings.HasPrefix(line, "data:") {
						hasData = true
					}
					if line != "" {
						continue
					}
					if event == "error" {
						fail(errors.New("feed emitted error event"))
						return
					}
					if !reported && event == "cursor" && hasData {
						reported = true
						outcome <- 1
					}
					event = ""
					hasData = false
				}
				if run.Err() == nil {
					fail(errors.New("feed ended before cancellation"))
				}
				return
			}
		})
		select {
		case result := <-outcome:
			switch result {
			case 1:
				admitted++
			case -1:
				rejected++
			default:
				err = errors.New("feed failed before admission")
			}
		case <-run.Done():
			err = errors.New("feed admission canceled or exceeded 30 seconds")
		}
		if err != nil {
			return stop, admitted, rejected, errors.Join(err, stop())
		}
	}
	if !deadline.Stop() || run.Err() != nil {
		return stop, admitted, rejected, errors.Join(errors.New("feed admission deadline/cancellation"), stop())
	}
	mu.Lock()
	err = errors.Join(failures...)
	mu.Unlock()
	if err != nil {
		err = errors.Join(err, stop())
	}
	return stop, admitted, rejected, err
}
