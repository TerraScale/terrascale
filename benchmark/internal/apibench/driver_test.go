package apibench

import (
	"bytes"
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestRunStepClosedCountsOK(t *testing.T) {
	t.Parallel()
	s := Step{Scenario: "doc.get", Transport: "sdk", PayloadBytes: 256, Mode: "closed", Concurrency: 4, StepS: 1, AbortErrorRate: 0.01}
	op := func(context.Context, int, int) string {
		time.Sleep(time.Millisecond)
		return "ok"
	}
	res := RunStep(context.Background(), s, op, &Meter{})
	if res.Ops == 0 || res.ErrorRate != 0 || res.Status["ok"] != res.OK || res.Stopped != "" {
		t.Fatalf("unexpected result %+v", res)
	}
}

func TestRunStepAbortsOnErrorRate(t *testing.T) {
	t.Parallel()
	s := Step{Scenario: "doc.get", Transport: "sdk", Mode: "closed", Concurrency: 1, StepS: 2, AbortErrorRate: 0.01}
	op := func(_ context.Context, _, i int) string {
		time.Sleep(time.Millisecond)
		if i%2 == 0 {
			return "429"
		}
		return "ok"
	}
	res := RunStep(context.Background(), s, op, &Meter{})
	if res.Stopped != "error rate" || res.DurationS >= 2 {
		t.Fatalf("want early error-rate stop, got stopped %q over %.2fs", res.Stopped, res.DurationS)
	}
}

func TestResultStatusIsDetached(t *testing.T) {
	t.Parallel()
	now := time.Now()
	r := newRecorder(Step{Mode: "closed", Concurrency: 1, StepS: 1}, now, now.Add(time.Hour))
	r.observe(now, now, "ok")
	res := r.result()
	r.observe(now, now, "ok")
	if res.Status["ok"] != 1 {
		t.Fatalf("result changed after return: status %+v", res.Status)
	}
}

func TestRunStepOpenRecordsDropped(t *testing.T) {
	t.Parallel()
	s := Step{Scenario: "doc.get", Transport: "sdk", Mode: "open", Concurrency: 1, Rate: 100, StepS: 1, AbortErrorRate: 0.99}
	op := func(context.Context, int, int) string {
		time.Sleep(50 * time.Millisecond)
		return "ok"
	}
	res := RunStep(context.Background(), s, op, &Meter{})
	if res.Status["dropped"] == 0 || res.Status["ok"] == 0 {
		t.Fatalf("want drops and completions with cap 1, got %+v", res.Status)
	}
}

func TestRestartExcludesSetupFromLatency(t *testing.T) {
	t.Parallel()
	s := Step{Scenario: "doc.delete", Transport: "sdk", Mode: "closed", Concurrency: 1, StepS: 1, AbortErrorRate: 0.99}
	op := func(ctx context.Context, _, _ int) string {
		time.Sleep(20 * time.Millisecond)
		Restart(ctx)
		return "ok"
	}
	res := RunStep(context.Background(), s, op, &Meter{})
	if res.OK == 0 || res.Latency.Max >= 10_000 {
		t.Fatalf("setup was timed: ok %d, max %vµs", res.OK, res.Latency.Max)
	}
}

func TestMeterCountsBodyBytes(t *testing.T) {
	t.Parallel()
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = io.ReadAll(r.Body)
		_, _ = w.Write(make([]byte, 1000))
	}))
	defer srv.Close()
	var m Meter
	c := &http.Client{Transport: m.Wrap(http.DefaultTransport)}
	resp, err := c.Post(srv.URL, "application/octet-stream", bytes.NewReader(make([]byte, 500)))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := io.ReadAll(resp.Body); err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if out, in := m.out.Load(), m.in.Load(); out != 500 || in != 1000 {
		t.Fatalf("bytes out %d in %d; want 500 and 1000", out, in)
	}
}
