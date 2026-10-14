package main

import (
	"bytes"
	"cmp"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// shapeBody is the part of a shape response the follower reads (shape-reads.md section 6): the handle to continue on,
// the offset to continue from and the operations to apply in order.
type shapeBody struct {
	Handle     string `json:"handle"`
	NextOffset string `json:"next_offset"`
	UpToDate   bool   `json:"up_to_date"`
	Changes    []struct {
		Ops []json.RawMessage `json:"ops"`
	} `json:"changes"`
}

// shapeResponse is one response: its status, headers and whole body.
type shapeResponse struct {
	status int
	header http.Header
	body   []byte
}

// shapeRetryWait is the pause before a follower repeats a request that the server refused as busy or unavailable.
const shapeRetryWait = 200 * time.Millisecond

// qosWait is the pause before a follower repeats a request the plan's request budget refused (429 tenant_busy): the
// server's Retry-After, never less than shapeRetryWait.
func qosWait(h http.Header) time.Duration {
	wait := shapeRetryWait
	if s, err := strconv.Atoi(h.Get("Retry-After")); err == nil {
		wait = max(wait, time.Duration(s)*time.Second)
	}
	return wait
}

// followShape runs one shape follower through the client path of shape-reads.md: a bootstrap at offset=-1 answers 307 to
// the newest snapshot's first page on the follower's handle, then pages, chunks and live long-polls at the sealed head.
// The first 200 body admits the follower. A refusal before admission (429 shape_busy, 503 shape_too_large) is a rejection
// and ends it; after admission, a refusal the protocol says to retry (429 shape_busy, 503 unavailable or shape_too_large)
// repeats the same request with its state kept, and each repeat is counted. A 429 tenant_busy is the plan's request
// budget refusing the request: it is repeated after Retry-After, before or after admission, and counted in qosBusy. Any
// other refusal, a stale handle or a malformed body is an invalid measurement.
func (b *bench) followShape(ctx context.Context, m *measure, outcomes chan<- string) {
	var (
		admitted, redirected, atHead bool
		handle, bucket               string
		seen                         = map[string]bool{}
		target                       = b.shapeURL(url.Values{"offset": {"-1"}})
	)
	fail := func(err error) {
		if ctx.Err() == nil {
			m.failures.Add(1)
			m.fail(err)
		}
		if !admitted {
			outcomes <- ""
		}
	}
	for {
		resp, err := b.shapeGet(ctx, m, target)
		if err != nil {
			fail(err)
			return
		}
		switch {
		case resp.status == http.StatusTemporaryRedirect && !redirected:
			handle = resp.header.Get("TB-Shape-Handle")
			loc := resp.header.Get("Location")
			if handle == "" || loc == "" {
				fail(fmt.Errorf("bootstrap 307 without Location or TB-Shape-Handle"))
				return
			}
			redirected = true
			target = b.origin + loc
		case resp.status == http.StatusOK && redirected:
			var body shapeBody
			if err := json.Unmarshal(resp.body, &body); err != nil {
				fail(fmt.Errorf("shape body: %w", err))
				return
			}
			if body.Handle != handle {
				fail(fmt.Errorf("shape body names handle %q, want %q", body.Handle, handle))
				return
			}
			at := time.Now()
			for _, c := range body.Changes {
				for _, op := range c.Ops {
					if err := m.deliver(seen, op, at); err != nil {
						fail(err)
						return
					}
				}
			}
			if !admitted {
				admitted = true
				outcomes <- "admitted"
			}
			if v := resp.header.Get("TB-Shape-Cursor"); v != "" {
				bucket = v
			}
			q := url.Values{"handle": {handle}, "offset": {body.NextOffset}}
			if isDecimal(body.NextOffset) && body.UpToDate {
				// At the sealed head: long-poll for the next commit on the bucket the last live answer named.
				q.Set("live", "true")
				if bucket != "" {
					q.Set("cursor", bucket)
				}
				if !atHead {
					atHead = true
					m.atHead.Add(1)
				}
			}
			target = b.shapeURL(q)
		default:
			code := errorCode(bytes.NewReader(resp.body))
			if resp.status == http.StatusTooManyRequests && code == "tenant_busy" {
				m.qosBusy.Add(1)
				if !sleep(ctx, qosWait(resp.header)) {
					return
				}
				continue
			}
			if !admitted && (resp.status == http.StatusTooManyRequests && code == "shape_busy" || resp.status == http.StatusServiceUnavailable && code == "shape_too_large") {
				// The live load (load.go) repeats a refusal until b.admitBy, as a client would; B2 leaves admitBy zero.
				if time.Now().Before(b.admitBy) {
					m.refused.Add(1)
					if !sleep(ctx, shapeRetryWait) {
						return
					}
					continue
				}
				outcomes <- "rejected_" + code
				return
			}
			if !admitted || !(resp.status == http.StatusTooManyRequests && code == "shape_busy" ||
				resp.status == http.StatusServiceUnavailable && (code == "unavailable" || code == "shape_too_large")) {
				fail(fmt.Errorf("%s: HTTP %d %s", target, resp.status, resp.body))
				return
			}
			m.retries.Add(1)
			if !sleep(ctx, shapeRetryWait) {
				return
			}
		}
	}
}

// shapeGet is one request with the follower's bearer token. Its body counts toward the window's bytes.
func (b *bench) shapeGet(ctx context.Context, m *measure, target string) (shapeResponse, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, target, nil)
	if err != nil {
		return shapeResponse{}, err
	}
	req.Header.Set("Authorization", "Bearer "+b.token)
	resp, err := b.shape.Do(req)
	if err != nil {
		return shapeResponse{}, err
	}
	defer resp.Body.Close()
	m.codes[min(resp.StatusCode, len(m.codes)-1)].Add(1)
	body, err := io.ReadAll(countingReader{resp.Body, &m.bytes})
	if err != nil {
		return shapeResponse{}, err
	}
	return shapeResponse{status: resp.StatusCode, header: resp.Header, body: body}, nil
}

// shapeURL is the collection's shape route with q as its query. The bootstrap is the only request without a handle.
func (b *bench) shapeURL(q url.Values) string {
	return b.database + "/collections/" + b.collection + "/shape?" + q.Encode()
}

// isDecimal reports whether an offset is a commit boundary (decimal) rather than a snapshot page (snap.<S>.<P>).
func isDecimal(offset string) bool {
	_, err := strconv.ParseUint(offset, 10, 64)
	return err == nil && !strings.HasPrefix(offset, "snap.")
}

// shapeReadyTimeout bounds a shape step's readiness wait; shapeReadySettle is how long the node's waiting gauge must
// hold still before the step opens its window.
const (
	shapeReadyTimeout = 60 * time.Second
	shapeReadySettle  = time.Second
)

// waitParked is the shape step's readiness wait; see awaitParked.
func waitParked(b *bench, ctx context.Context, m *measure, admitted int64) (int64, error) {
	return awaitParked(ctx, b.metrics, m, admitted, shapeReadyTimeout)
}

// awaitParked waits until a shape step may open its window. Before a step (admitted 0) the node must hold no parked
// requester left from an earlier step. Otherwise every admitted follower must have sent its first live poll
// (m.atHead), and the node's terrabase.shape.requests.waiting gauge, which counts only requesters parked at the head,
// must hold still for shapeReadySettle at one or more. The gauge is the node's own count and can sit below the admitted
// count: the server caps live requesters at 256 per machine, and the rest retry on shape_busy. That is reported as
// server_at_ready, not waited for. A follower that failed ends the wait. It returns the gauge at readiness. A timeout
// after admission is a capacityLimit; a timeout before a step is an invalid measurement.
func awaitParked(ctx context.Context, metricsURL string, m *measure, admitted int64, timeout time.Duration) (int64, error) {
	var (
		last  int64 = -1
		since time.Time
	)
	for deadline := time.Now().Add(timeout); ; {
		if err := cmp.Or(m.firstErr(), ctx.Err()); err != nil {
			return 0, err
		}
		body, err := get(ctx, metricsClient, metricsURL)
		if err != nil {
			return 0, err
		}
		waiting, err := shapeWaiting(body)
		if err != nil {
			return 0, err
		}
		if waiting != last {
			last, since = waiting, time.Now()
		}
		if admitted == 0 && waiting == 0 {
			return 0, nil
		}
		if admitted > 0 && m.atHead.Load() == admitted && waiting > 0 && time.Since(since) >= shapeReadySettle {
			return waiting, nil
		}
		if time.Now().After(deadline) {
			reason := fmt.Sprintf("shape step not ready within %s: %d of %d admitted followers sent a live poll, the node holds %d parked requesters", timeout, m.atHead.Load(), admitted, waiting)
			if admitted == 0 {
				return 0, errors.New(reason)
			}
			return 0, capacityLimit(reason)
		}
		if !sleep(ctx, 200*time.Millisecond) {
			return 0, ctx.Err()
		}
	}
}

// shapeWaiting is the node's terrabase.shape.requests.waiting. The gauge is absent until a shape request has parked, and
// it is then zero.
func shapeWaiting(body string) (int64, error) {
	lines := promLines(body, "terrabase_shape_requests_waiting")
	switch len(lines) {
	case 0:
		return 0, nil
	case 1:
		v, ok := promValue(lines[0], "terrabase_shape_requests_waiting", "")
		if !ok {
			return 0, fmt.Errorf("metrics: unreadable terrabase_shape_requests_waiting sample: %q", lines[0])
		}
		return int64(v), nil
	default:
		return 0, fmt.Errorf("metrics: want one terrabase_shape_requests_waiting series, found %d", len(lines))
	}
}
