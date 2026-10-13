package main

import (
	"bufio"
	"bytes"
	"cmp"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/HdrHistogram/hdrhistogram-go"
	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/apibench"
	"terrascale.tech/ts-benchmark/internal/replbench"
)

type bench struct {
	pid        int
	collection string
	database   string // the database's URL
	target     target // the step's target
	token      string
	writer     *replbench.Region
	sse        *http.Client
	metrics    string
}

// probe holds a step's raw metric lines, for the report.
type probe struct {
	n             int
	target        string
	before, after string
}

// newBench creates the run's collection and finds the node's metrics listener.
func newBench(ctx context.Context, t replbench.Targets) (*bench, error) {
	w, err := replbench.NewRegion(region, t, writeRate*4)
	if err != nil {
		return nil, err
	}
	b := &bench{
		pid:        t.PID,
		collection: "shape-" + strings.ToLower(ulid.Make().String()),
		token:      t.Regions[region].Token,
		writer:     w,
		// One connection per stream: a rejected follower's connection closes with it.
		sse: &http.Client{Transport: &http.Transport{DisableKeepAlives: true}},
	}
	b.database = strings.TrimSuffix(t.Regions[region].URL, "/") + "/v1/projects/" + t.Project + "/databases/" + t.Database
	body, _ := json.Marshal(map[string]string{"name": b.collection})
	resp, err := w.Do(ctx, http.MethodPost, "/collections", body, "application/json")
	if err != nil {
		return nil, err
	}
	if resp.Status/100 != 2 {
		return nil, fmt.Errorf("create collection: HTTP %d %s", resp.Status, resp.Body)
	}
	if err = b.findMetrics(ctx); err != nil {
		return nil, err
	}
	return b, nil
}

// findMetrics identifies gru's scoped exporter without changing the shared host's targets format. No writer runs
// during this calibration stream, and it closes before any measured step starts.
func (b *bench) findMetrics(ctx context.Context) error {
	ctx, stop := context.WithTimeout(ctx, 30*time.Second)
	defer stop()
	b.target = targets[0]
	m := &measure{}
	outcomes := make(chan string, 1)
	var wg sync.WaitGroup
	wg.Go(func() { b.followChanges(ctx, m, outcomes) })
	defer func() { stop(); wg.Wait() }()
	select {
	case outcome := <-outcomes:
		if outcome != "admitted" {
			return fmt.Errorf("metrics calibration stream: %s: %v", outcome, m.firstErr())
		}
	case <-ctx.Done():
		return fmt.Errorf("metrics calibration stream: %w", ctx.Err())
	}
	var err error
	b.metrics, err = metricsURL(ctx, b.pid)
	if err != nil {
		return err
	}
	return m.firstErr()
}

// measure is one step's delivery record.
type measure struct {
	bytes atomic.Int64 // SSE body bytes of admitted streams

	mu         sync.Mutex
	start, end time.Time // the recorded window, by write send time
	deadline   time.Time // deliveries read after it do not count
	ok, events int64     // first deliveries; every received upsert of an in-window write
	hist       *hdrhistogram.Histogram
	err        error
}

func (m *measure) fail(err error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.err == nil {
		m.err = err
	}
}

type changeEvent struct {
	ID       string `json:"id"`
	Op       string `json:"op"`
	Document *struct {
		T int64 `json:"t"`
	} `json:"document"`
}

// deliver records one change event a follower read at at; seen is that follower's delivered ids (delivery is at least once).
func (m *measure) deliver(seen map[string]bool, data []byte, at time.Time) error {
	var e changeEvent
	if err := json.Unmarshal(data, &e); err != nil {
		return fmt.Errorf("change event %q: %w", data, err)
	}
	if e.Op != "upsert" || e.Document == nil || e.Document.T == 0 {
		return fmt.Errorf("change event %q: want an upsert of a writer document", data)
	}
	sent := time.Unix(0, e.Document.T)
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.start.IsZero() || sent.Before(m.start) || !sent.Before(m.end) || at.After(m.deadline) {
		return nil
	}
	m.events++
	if seen[e.ID] {
		return nil
	}
	seen[e.ID] = true
	m.ok++
	m.hist.RecordValue(min(max(at.Sub(sent).Microseconds(), 1), 60_000_000))
	return nil
}

// step runs st.Concurrency followers against the steady writer. It answers an error for an invalid measurement:
// a follower refused other than by changes_busy or that fails, a stream that ends early, a failed or late write, a
// server stream count that disagrees with the admitted followers, or a window without writes or deliveries.
func (b *bench) step(ctx context.Context, st apibench.Step) (apibench.Result, probe, error) {
	n := st.Concurrency
	p := probe{n: n, target: st.Scenario}
	fail := func(err error) (apibench.Result, probe, error) { return apibench.Result{}, p, err }
	i := slices.IndexFunc(targets, func(t target) bool { return t.name == st.Scenario })
	if i < 0 {
		return fail(fmt.Errorf("unknown target %q", st.Scenario))
	}
	b.target = targets[i]
	if err := b.target.waitReady(b, ctx, 0); err != nil {
		return fail(err)
	}
	before, err := get(ctx, metricsClient, b.metrics)
	if err != nil {
		return fail(err)
	}
	p.before = strings.Join(promLines(before, "terrabase_stats_operations"), "\n") + "\n"
	readsBefore, ok := promValue(before, "terrabase_stats_operations", `kind="read"`)
	if !ok {
		return fail(fmt.Errorf("metrics: no terrabase_stats_operations{kind=\"read\"}"))
	}

	m := &measure{hist: hdrhistogram.New(1, 60_000_000, 3)}
	fctx, stop := context.WithCancel(ctx)
	var wg sync.WaitGroup
	defer func() {
		stop()
		wg.Wait()
	}()
	// Paced and one at a time: the local tenant's Hobby QoS plan refuses bursts, and an admitted stream gives back its
	// QoS slot once its first cursor is sent. Admitted streams stay open, so all of them read together afterwards.
	outcomes := make(chan string, n) // "admitted" or "rejected_changes_busy"; "" when the follower failed (m.err)
	status := map[string]int64{}
	t0, deadline := time.Now(), time.After(admissionDeadline)
	for i := range n {
		sleep(ctx, time.Until(t0.Add(time.Duration(i)*time.Second/admitRate)))
		wg.Go(func() { b.target.follow(b, fctx, m, outcomes) })
		select {
		case o := <-outcomes:
			status[o]++
		case <-deadline:
			m.fail(fmt.Errorf("only %d of %d followers admitted or rejected within %s", i, n, admissionDeadline))
		}
		if err := cmp.Or(m.firstErr(), ctx.Err()); err != nil {
			return fail(err)
		}
	}
	admitted := status["admitted"]
	if admitted == 0 {
		return fail(fmt.Errorf("no follower of %d admitted", n))
	}
	if err := b.target.waitReady(b, ctx, admitted); err != nil {
		return fail(err)
	}

	t0 = time.Now()
	m.mu.Lock()
	m.start = t0.Add(time.Duration(st.WarmupS) * time.Second)
	m.end = m.start.Add(time.Duration(st.StepS) * time.Second)
	m.deadline = m.end.Add(grace)
	start, end, cutoff := m.start, m.end, m.deadline
	m.mu.Unlock()
	var writes atomic.Int64
	written := make(chan error, 1)
	// A write still unanswered at the cutoff fails the step.
	wctx, wcancel := context.WithDeadline(fctx, cutoff)
	defer wcancel()
	go func() { written <- b.write(wctx, t0, start, end, &writes) }()
	sleep(ctx, time.Until(start))
	bytes0 := m.bytes.Load()
	sampler := replbench.StartLocalSampler(b.pid, nil)
	sleep(ctx, time.Until(end))
	bytes1 := m.bytes.Load()
	res := sampler.Stop().Nodes["all nodes (one process)"]
	if err := <-written; err != nil {
		m.fail(err)
	}
	sleep(ctx, time.Until(cutoff))
	if err := cmp.Or(m.firstErr(), ctx.Err()); err != nil {
		return fail(err)
	}
	stop()
	wg.Wait()

	// The read counter moves once per closed 5 s window: wait one out so the step's reads are in it.
	sleep(ctx, 6*time.Second)
	after, err := get(ctx, metricsClient, b.metrics)
	if err != nil {
		return fail(err)
	}
	p.after = strings.Join(promLines(after, "terrabase_stats_operations"), "\n") + "\n"
	readsAfter, ok := promValue(after, "terrabase_stats_operations", `kind="read"`)
	if !ok {
		return fail(fmt.Errorf("metrics: no terrabase_stats_operations{kind=\"read\"}"))
	}

	m.mu.Lock()
	defer m.mu.Unlock()
	w := writes.Load()
	expected := admitted * w
	switch {
	case w == 0:
		return fail(fmt.Errorf("no write in the window"))
	case m.ok == 0:
		return fail(fmt.Errorf("no delivery of %d expected", expected))
	case m.ok > expected:
		return fail(fmt.Errorf("%d deliveries, more than the %d expected", m.ok, expected))
	case res.CPUCores.N == 0:
		return fail(fmt.Errorf("no CPU sample of process %d", b.pid))
	}
	status["writes"] = w
	status["accounting_reads_delta"] = int64(readsAfter - readsBefore)
	status["inferred_min_doc_selects"] = m.events
	d := float64(st.StepS)
	return apibench.Result{
		Type:         "result",
		Scenario:     st.Scenario,
		Transport:    st.Transport,
		PayloadBytes: st.PayloadBytes,
		Mode:         "open",
		Concurrency:  n,
		Rate:         writeRate,
		Start:        start.UTC(),
		DurationS:    d,
		Ops:          expected,
		OK:           m.ok,
		Errors:       expected - m.ok,
		ErrorRate:    float64(expected-m.ok) / float64(expected),
		RPS:          float64(m.ok) / d,
		Status:       status,
		Latency: apibench.Latency{
			P50:  float64(m.hist.ValueAtQuantile(50)),
			P90:  float64(m.hist.ValueAtQuantile(90)),
			P99:  float64(m.hist.ValueAtQuantile(99)),
			P999: float64(m.hist.ValueAtQuantile(99.9)),
			Max:  float64(m.hist.Max()),
			Mean: m.hist.Mean(),
		},
		BytesIn: bytes1 - bytes0,
		CPUS:    res.CPUCores.Avg * d,
		RSSMB:   res.MemMB.Max,
	}, p, nil
}

func (m *measure) firstErr() error {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.err
}

// followChanges opens one feed stream and reports its admission on outcomes, then reads it until ctx ends.
func (b *bench) followChanges(ctx context.Context, m *measure, outcomes chan<- string) {
	report := func(err error) {
		m.fail(err)
		outcomes <- ""
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, b.database+b.target.path+"?cursor=now&collections="+url.QueryEscape(b.collection), nil)
	if err != nil {
		report(err)
		return
	}
	req.Header.Set("Authorization", "Bearer "+b.token)
	req.Header.Set("Accept", "text/event-stream")
	resp, err := b.sse.Do(req)
	if err != nil {
		report(err)
		return
	}
	defer resp.Body.Close()
	switch resp.StatusCode {
	case http.StatusOK:
	default:
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
		// Only the feed's own cap is an expected rejection; any other refusal (tenant_memory, storage_busy, QoS)
		// invalidates the admission baseline.
		if code := errorCode(bytes.NewReader(body)); resp.StatusCode == http.StatusTooManyRequests && code == "changes_busy" {
			outcomes <- "rejected_changes_busy"
			return
		}
		report(fmt.Errorf("%s: HTTP %d %s", b.target.path, resp.StatusCode, body))
		return
	}
	started := false
	seen := map[string]bool{}
	err = readSSE(countingReader{resp.Body, &m.bytes}, func(event string, data []byte) error {
		switch event {
		case "cursor":
			if !started {
				started = true
				outcomes <- "admitted"
			}
		case "change":
			if !started {
				return fmt.Errorf("change event before the first cursor")
			}
			return m.deliver(seen, data, time.Now())
		case "error":
			return fmt.Errorf("stream error event: %s", data)
		}
		return nil
	})
	switch {
	case !started:
		report(fmt.Errorf("stream ended before its first cursor: %v", err))
	case ctx.Err() == nil:
		m.fail(fmt.Errorf("stream ended during the step: %v", err))
	}
}

// write PUTs one document every 1/writeRate s from t0 until end, counting those sent inside [start, end) in inWindow.
// Each document holds its send time. Any failed write is an error.
func (b *bench) write(ctx context.Context, t0, start, end time.Time, inWindow *atomic.Int64) error {
	var wg sync.WaitGroup
	var once sync.Once
	var failed error
	for i := 0; ; i++ {
		at := t0.Add(time.Duration(i) * time.Second / writeRate)
		if !at.Before(end) || !sleep(ctx, time.Until(at)) {
			break
		}
		wg.Go(func() {
			sent := time.Now()
			resp, err := b.writer.Do(ctx, http.MethodPut, replbench.DocPath(b.collection, ulid.Make().String()), document(sent), "application/json")
			switch {
			case err != nil:
				once.Do(func() { failed = fmt.Errorf("write: %w", err) })
			case resp.Status/100 != 2:
				once.Do(func() { failed = fmt.Errorf("write: HTTP %d %s", resp.Status, resp.Body) })
			case !sent.Before(start) && sent.Before(end):
				inWindow.Add(1)
			}
		})
	}
	wg.Wait()
	if failed == nil && ctx.Err() != nil {
		return ctx.Err()
	}
	return failed
}

// document is a payloadBytes JSON document holding its send time in t.
func document(sent time.Time) []byte {
	b := fmt.Appendf(nil, `{"t":%d,"pad":"`, sent.UnixNano())
	b = append(b, bytes.Repeat([]byte("x"), payloadBytes-len(b)-2)...)
	return append(b, `"}`...)
}

var metricsClient = &http.Client{Timeout: 5 * time.Second}

// waitStreams waits up to 30 s for the node's open feed streams (terrabase.changes.streams, absent before the first
// stream) to equal want.
func (b *bench) waitStreams(ctx context.Context, want int64) error {
	var got float64
	for deadline := time.Now().Add(30 * time.Second); time.Now().Before(deadline); sleep(ctx, 200*time.Millisecond) {
		body, err := get(ctx, metricsClient, b.metrics)
		if err != nil {
			return err
		}
		got, _ = promValue(body, "terrabase_changes_streams", "")
		if int64(got) == want {
			return nil
		}
	}
	return fmt.Errorf("the node has %v open feed streams, want %d", got, want)
}

// readSSE calls on with each event's type and data until r ends. A field's value follows its colon, less one leading
// space; lines starting with a colon are comments.
func readSSE(r io.Reader, on func(event string, data []byte) error) error {
	sc := bufio.NewScanner(r)
	sc.Buffer(make([]byte, 64<<10), 8<<20)
	var event string
	var data []byte
	hasData := false
	for sc.Scan() {
		line := sc.Bytes()
		if len(line) == 0 {
			if event != "" || hasData {
				if err := on(cmp.Or(event, "message"), data); err != nil {
					return err
				}
			}
			event, data, hasData = "", nil, false
			continue
		}
		field, value, _ := bytes.Cut(line, []byte(":"))
		value = bytes.TrimPrefix(value, []byte(" "))
		switch string(field) {
		case "event":
			event = string(value)
		case "data":
			if hasData {
				data = append(data, '\n')
			}
			data, hasData = append(data, value...), true
		}
	}
	return sc.Err()
}

// errorCode is the code of a TerraBase error body, or "unknown".
func errorCode(r io.Reader) string {
	var body struct {
		Error struct {
			Code string `json:"code"`
		} `json:"error"`
	}
	if json.NewDecoder(io.LimitReader(r, 4096)).Decode(&body) != nil || body.Error.Code == "" {
		return "unknown"
	}
	return body.Error.Code
}

type countingReader struct {
	io.Reader
	n *atomic.Int64
}

func (c countingReader) Read(p []byte) (int, error) {
	n, err := c.Reader.Read(p)
	c.n.Add(int64(n))
	return n, err
}

func sleep(ctx context.Context, d time.Duration) bool {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
	case <-t.C:
	}
	return ctx.Err() == nil
}
