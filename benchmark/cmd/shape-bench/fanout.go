package main

import (
	"bufio"
	"bytes"
	"cmp"
	"context"
	"encoding/json"
	"errors"
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
	"terrascale.tech/ts-benchmark/internal/benchclient"
)

type bench struct {
	pid        int
	collection string
	origin     string
	database   string
	target     target
	token      string
	writer     *benchclient.Region
	sse        *http.Client
	shape      *http.Client
	metrics    string
	admitBy    time.Time
}

type probe struct {
	n             int
	target        string
	before, after string
}

func newBench(ctx context.Context, t benchclient.Targets) (*bench, error) {
	w, err := benchclient.NewRegion(region, t, writeRate*4)
	if err != nil {
		return nil, err
	}
	b := &bench{
		pid:        t.PID,
		collection: "shape-" + strings.ToLower(ulid.Make().String()),
		origin:     strings.TrimSuffix(t.Regions[region].URL, "/"),
		token:      t.Regions[region].Token,
		writer:     w,

		sse: &http.Client{Transport: &http.Transport{DisableKeepAlives: true}},
		shape: &http.Client{Transport: &http.Transport{DisableKeepAlives: true}, CheckRedirect: func(*http.Request, []*http.Request) error {
			return http.ErrUseLastResponse
		}},
	}
	b.database = b.origin + "/v1/projects/" + t.Project + "/databases/" + t.Database
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

type measure struct {
	bytes    atomic.Int64
	atHead   atomic.Int64
	retries  atomic.Int64
	qosBusy  atomic.Int64
	refused  atomic.Int64
	failures atomic.Int64
	codes    [600]atomic.Int64
	reasons  sync.Map

	mu         sync.Mutex
	start, end time.Time
	deadline   time.Time
	ok, events int64
	hist       *hdrhistogram.Histogram
	err        error
}

type capacityLimit string

func (c capacityLimit) Error() string { return string(c) }

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

func (b *bench) step(ctx context.Context, st apibench.Step) (apibench.Result, probe, error) {
	n := st.Concurrency
	p := probe{n: n, target: st.Scenario}
	fail := func(err error) (apibench.Result, probe, error) { return apibench.Result{}, p, err }
	i := slices.IndexFunc(targets, func(t target) bool { return t.name == st.Scenario })
	if i < 0 {
		return fail(fmt.Errorf("unknown target %q", st.Scenario))
	}
	b.target = targets[i]
	if _, err := b.target.waitReady(b, ctx, &measure{}, 0); err != nil {
		return fail(err)
	}

	before, err := get(ctx, metricsClient, b.metrics)
	if err != nil {
		return fail(err)
	}
	p.before = rawSamples(before)
	readsBefore, ok := promValue(before, "terrabase_stats_operations", `kind="read"`)
	if !ok {
		return fail(fmt.Errorf("metrics: no terrabase_stats_operations{kind=\"read\"}"))
	}
	pathBefore, err := pathCounts(before, b.target.paths())
	if err != nil {
		return fail(err)
	}

	m := &measure{hist: hdrhistogram.New(1, 60_000_000, 3)}
	fctx, stop := context.WithCancel(ctx)
	var wg sync.WaitGroup
	defer func() {
		stop()
		wg.Wait()
	}()

	outcomes := make(chan string, n)
	status := map[string]int64{}
	t0, deadline := time.Now(), time.After(admissionDeadline)

	limited := func(reason error) (apibench.Result, probe, error) {
		status["qos_busy_followers"] = m.qosBusy.Load()
		status["retries"] = m.retries.Load()
		return apibench.Result{Type: "result", Scenario: st.Scenario, Transport: st.Transport, PayloadBytes: st.PayloadBytes,
			Mode: "open", Concurrency: n, Start: t0.UTC(), Status: status, Stopped: reason.Error()}, p, nil
	}
	for i := range n {
		sleep(ctx, time.Until(t0.Add(time.Duration(i)*time.Second/admitRate)))
		wg.Go(func() { b.target.follow(b, fctx, m, outcomes) })
		select {
		case o := <-outcomes:
			status[o]++
		case <-ctx.Done():
			return fail(cmp.Or(m.firstErr(), ctx.Err()))
		case <-deadline:
			if err := cmp.Or(m.firstErr(), ctx.Err()); err != nil {
				return fail(err)
			}
			status["unresolved"] = int64(n - i)
			return limited(fmt.Errorf("only %d of %d followers admitted or rejected within %s", i, n, admissionDeadline))
		}
		if err := cmp.Or(m.firstErr(), ctx.Err()); err != nil {
			return fail(err)
		}
	}
	admitted := status["admitted"]
	if admitted == 0 {
		return limited(fmt.Errorf("no follower of %d admitted", n))
	}
	atReady, err := b.target.waitReady(b, ctx, m, admitted)
	var limit capacityLimit
	switch {
	case errors.As(err, &limit):
		if err := cmp.Or(m.firstErr(), ctx.Err()); err != nil {
			return fail(err)
		}
		return limited(limit)
	case err != nil:
		return fail(err)
	}

	t0 = time.Now()
	m.mu.Lock()
	m.start = t0.Add(time.Duration(st.WarmupS) * time.Second)
	m.end = m.start.Add(time.Duration(st.StepS) * time.Second)
	m.deadline = m.end.Add(grace)
	start, end, cutoff := m.start, m.end, m.deadline
	m.mu.Unlock()
	var writes, writeQoS atomic.Int64
	written := make(chan error, 1)

	wctx, wcancel := context.WithDeadline(fctx, cutoff)
	defer wcancel()
	go func() { written <- b.write(wctx, t0, start, end, &writes, &writeQoS) }()
	sleep(ctx, time.Until(start))
	bytes0 := m.bytes.Load()
	sampler := benchclient.StartLocalSampler(b.pid, nil)
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

	sleep(ctx, 6*time.Second)
	after, err := get(ctx, metricsClient, b.metrics)
	if err != nil {
		return fail(err)
	}
	p.after = rawSamples(after)
	readsAfter, ok := promValue(after, "terrabase_stats_operations", `kind="read"`)
	if !ok {
		return fail(fmt.Errorf("metrics: no terrabase_stats_operations{kind=\"read\"}"))
	}
	storageDeltas, err := pathDeltas(pathBefore, after, b.target.paths())
	if err != nil {
		return fail(err)
	}

	m.mu.Lock()
	defer m.mu.Unlock()
	w := writes.Load()
	expected := admitted * w
	var stopped string
	switch {
	case w == 0:
		return fail(fmt.Errorf("no write in the window"))
	case res.CPUCores.N == 0:
		return fail(fmt.Errorf("no CPU sample of process %d", b.pid))
	case m.ok == 0:

		stopped = fmt.Sprintf("no delivery of %d expected", expected)
	case m.ok > expected:
		return fail(fmt.Errorf("%d deliveries, more than the %d expected", m.ok, expected))
	}
	status["writes"] = w
	status["server_at_ready"] = atReady

	status["qos_busy_followers"] = m.qosBusy.Load()
	status["qos_busy_writes"] = writeQoS.Load()
	for _, rd := range b.target.reads {
		status[rd.key] = storageDeltas[rd.path]
	}
	status["accounting_reads_delta"] = int64(readsAfter - readsBefore)
	if b.target.inferred {
		status["inferred_min_doc_selects"] = m.events
	}
	status["retries"] = m.retries.Load()
	d := float64(st.StepS)

	var lat apibench.Latency
	if m.ok > 0 {
		lat = apibench.Latency{
			P50:  float64(m.hist.ValueAtQuantile(50)),
			P90:  float64(m.hist.ValueAtQuantile(90)),
			P99:  float64(m.hist.ValueAtQuantile(99)),
			P999: float64(m.hist.ValueAtQuantile(99.9)),
			Max:  float64(m.hist.Max()),
			Mean: m.hist.Mean(),
		}
	}
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
		Latency:      lat,
		BytesIn:      bytes1 - bytes0,
		CPUS:         res.CPUCores.Avg * d,
		RSSMB:        res.MemMB.Max,
		Stopped:      stopped,
	}, p, nil
}

func (m *measure) countReason(status int, body []byte) {
	var e struct {
		Error struct{ Code, Message string }
	}
	_ = json.Unmarshal(body, &e)
	n, _ := m.reasons.LoadOrStore(fmt.Sprintf("%d %s: %s", status, e.Error.Code, e.Error.Message), new(atomic.Int64))
	n.(*atomic.Int64).Add(1)
}

func (m *measure) firstErr() error {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.err
}

func (b *bench) followChanges(ctx context.Context, m *measure, outcomes chan<- string) {
	report := func(err error) {
		m.fail(err)
		outcomes <- ""
	}
	var resp *http.Response
	for {
		r, err := b.openFeed(ctx)
		if err != nil {
			report(err)
			return
		}
		if r.StatusCode == http.StatusOK {
			resp = r
			break
		}
		body, _ := io.ReadAll(io.LimitReader(r.Body, 4096))
		r.Body.Close()
		code := errorCode(bytes.NewReader(body))
		switch {
		case r.StatusCode == http.StatusTooManyRequests && code == "tenant_busy":
			m.qosBusy.Add(1)
			if !sleep(ctx, qosWait(r.Header)) {
				return
			}
		case r.StatusCode == http.StatusTooManyRequests && code == "changes_busy":
			outcomes <- "rejected_changes_busy"
			return
		default:

			report(fmt.Errorf("%s: HTTP %d %s", b.target.path, r.StatusCode, body))
			return
		}
	}
	defer resp.Body.Close()
	started := false
	seen := map[string]bool{}
	err := readSSE(countingReader{resp.Body, &m.bytes}, func(event string, data []byte) error {
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

func (b *bench) openFeed(ctx context.Context) (*http.Response, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, b.database+b.target.path+"?cursor=now&collections="+url.QueryEscape(b.collection), nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Authorization", "Bearer "+b.token)
	req.Header.Set("Accept", "text/event-stream")
	return b.sse.Do(req)
}

func (b *bench) write(ctx context.Context, t0, start, end time.Time, inWindow, qosBusy *atomic.Int64) error {
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
			resp, err := b.put(ctx, benchclient.DocPath(b.collection, ulid.Make().String()), document(sent), qosBusy)
			switch {
			case err != nil:
				once.Do(func() {
					failed = fmt.Errorf("write sent at %s answered after %s: %w", sent.UTC().Format(time.RFC3339Nano), time.Since(sent), err)
				})
			case resp.Status/100 != 2:
				once.Do(func() {
					failed = fmt.Errorf("write sent at %s answered after %s: HTTP %d %s", sent.UTC().Format(time.RFC3339Nano), time.Since(sent), resp.Status, resp.Body)
				})
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

func (b *bench) put(ctx context.Context, path string, body []byte, qosBusy *atomic.Int64) (benchclient.Response, error) {
	for {
		resp, err := b.writer.Do(ctx, http.MethodPut, path, body, "application/json")
		if err != nil || resp.Status != http.StatusTooManyRequests || errorCode(bytes.NewReader(resp.Body)) != "tenant_busy" {
			return resp, err
		}
		qosBusy.Add(1)
		if !sleep(ctx, max(resp.RetryAfter, shapeRetryWait)) {
			return resp, ctx.Err()
		}
	}
}

func document(sent time.Time) []byte {
	b := fmt.Appendf(nil, `{"t":%d,"pad":"`, sent.UnixNano())
	b = append(b, bytes.Repeat([]byte("x"), payloadBytes-len(b)-2)...)
	return append(b, `"}`...)
}

var metricsClient = &http.Client{Timeout: 5 * time.Second}

func (b *bench) waitStreams(ctx context.Context, _ *measure, want int64) (int64, error) {
	var got float64
	for deadline := time.Now().Add(30 * time.Second); time.Now().Before(deadline); sleep(ctx, 200*time.Millisecond) {
		body, err := get(ctx, metricsClient, b.metrics)
		if err != nil {
			return 0, err
		}
		var ok bool
		got, ok = streamCount(body)
		if !ok && len(promLines(body, "terrabase_changes_streams")) != 0 {
			return 0, fmt.Errorf("metrics: want one terrabase_changes_streams series")
		}
		if int64(got) == want {
			return want, nil
		}
	}
	return 0, fmt.Errorf("the node has %v open feed streams, want %d", got, want)
}

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
