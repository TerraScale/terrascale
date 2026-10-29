package v1

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"strings"
	"testing"
	"time"
)

const prometheusProject = "01JA0000000000000000000001"

type prometheusTransport func(*http.Request) (*http.Response, error)

func (f prometheusTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

type prometheusRead func([]byte) (int, error)

func (f prometheusRead) Read(p []byte) (int, error) { return f(p) }

type prometheusBody struct {
	io.Reader
	closed bool
}

func (b *prometheusBody) Close() error { b.closed = true; return nil }

type prometheusSource struct {
	calls, invalidations int
	fetch                func(context.Context) (string, error)
}

func (s *prometheusSource) Token(ctx context.Context) (string, error) {
	s.calls++
	if s.fetch != nil {
		return s.fetch(ctx)
	}
	return "fixture.project.signature", nil
}
func (s *prometheusSource) Invalidate(string) { s.invalidations++ }

func prometheusClient(t *testing.T, source *prometheusSource, transport prometheusTransport) *Client {
	t.Helper()
	c, err := New(Config{BaseURL: "http://127.0.0.1/proxy", TokenSource: source, HTTPClient: &http.Client{Transport: transport}})
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func TestPrometheusPinnedRawContract(t *testing.T) {
	data, err := os.ReadFile("../contracts/project-metrics.json")
	if err != nil {
		t.Fatal(err)
	}
	var fixture struct{ Body, MediaType, Method, Path, Revision string }
	if err := json.Unmarshal(data, &fixture); err != nil {
		t.Fatal(err)
	}
	if fixture.Revision != "1d823d8534fc5035859a47e263691d0c7cb7b744" || fixture.MediaType != prometheusMedia {
		t.Fatal("wrong provenance")
	}
	source, calls := &prometheusSource{}, 0
	media := `Text/Plain; VERSION="0.0.4"; CHARSET="UTF-8"`
	bodies := []*prometheusBody{}
	c := prometheusClient(t, source, func(r *http.Request) (*http.Response, error) {
		calls++
		if r.Method != fixture.Method || r.URL.Path != "/proxy"+strings.Replace(fixture.Path, "{project}", prometheusProject, 1) || r.URL.RawQuery != "" || r.Body != nil || r.Header.Get("Accept") != fixture.MediaType || r.Header.Get("Authorization") != "Bearer fixture.project.signature" || r.Header.Get("X-TerraBase-Peer-Credential") != "" {
			t.Error("incorrect scrape request")
		}
		deadline, ok := r.Context().Deadline()
		if !ok || time.Until(deadline) > 30*time.Second {
			t.Error("no fixed deadline")
		}
		body := &prometheusBody{Reader: strings.NewReader(fixture.Body)}
		bodies = append(bodies, body)
		return &http.Response{StatusCode: 200, Header: http.Header{"Content-Type": {media}}, Body: body}, nil
	})
	for range 2 {
		out, err := c.ProjectPrometheusMetrics(t.Context(), prometheusProject)
		if err != nil || out.ContentType != media || string(out.Body) != fixture.Body {
			t.Fatalf("raw response: %v", err)
		}
	}
	if calls != 2 || source.calls != 2 || source.invalidations != 0 || !bodies[0].closed || !bodies[1].closed {
		t.Fatal("lifetime or replay failure")
	}
}

func TestPrometheusAPIErrorsSentOnce(t *testing.T) {
	for _, item := range []struct {
		status int
		code   string
	}{{400, "invalid_request"}, {401, "invalid_key"}, {403, "forbidden_scope"}, {404, "not_found"}, {421, "wrong_region"}, {503, "capacity"}} {
		source, calls := &prometheusSource{}, 0
		body := &prometheusBody{Reader: strings.NewReader(`{"error":{"code":"` + item.code + `","message":"refused"}}`)}
		c := prometheusClient(t, source, func(*http.Request) (*http.Response, error) {
			calls++
			return &http.Response{StatusCode: item.status, Body: body, Header: http.Header{"Content-Type": {"application/json"}, "X-Request-Id": {"correlation"}, "X-Terrabase-Database-Home": {"gru"}, "X-Terrabase-Database-Regions": {"gru,sjc"}, "Retry-After": {"120"}}}, nil
		})
		_, err := c.ProjectPrometheusMetrics(t.Context(), prometheusProject)
		var api *APIError
		if !errors.As(err, &api) || api.Status != item.status || string(api.Code) != item.code || api.RequestID != "correlation" || api.HomeRegion != "gru" || len(api.Regions) != 2 || api.RetryAfter != 120*time.Second || api.Header.Get("Content-Type") != "application/json" || !errors.Is(err, ErrorCode(item.code)) || calls != 1 || source.calls != 1 || source.invalidations != 0 || !body.closed {
			t.Fatalf("error=%v calls=%d", err, calls)
		}
	}
}

func TestPrometheusRejectsInvalidSuccessAndRedirect(t *testing.T) {
	for _, item := range []struct {
		status      int
		media, text string
		length      int64
	}{
		{204, prometheusMedia, "", 0}, {206, prometheusMedia, "partial", 7}, {302, prometheusMedia, "", 0},
		{200, "application/json", "{}", 2}, {200, "text/plain; charset=utf-8", "", 0},
		{200, "text/plain; version=1.0; charset=utf-8", "", 0}, {200, "text/plain; version=0.0.4; charset=utf-16", "", 0},
		{200, prometheusMedia, "\xff", -1}, {200, prometheusMedia, "", (8 << 20) + 1},
		{200, prometheusMedia, strings.Repeat("x", (8<<20)+1), -1},
	} {
		calls := 0
		body := &prometheusBody{Reader: strings.NewReader(item.text)}
		c := prometheusClient(t, &prometheusSource{}, func(*http.Request) (*http.Response, error) {
			calls++
			return &http.Response{StatusCode: item.status, ContentLength: item.length, Header: http.Header{"Content-Type": {item.media}, "Location": {"https://other.example/"}}, Body: body}, nil
		})
		_, err := c.ProjectPrometheusMetrics(t.Context(), prometheusProject)
		var protocol *PrometheusProtocolError
		var api *APIError
		if err == nil || (item.status == 302 && !errors.As(err, &api)) || (item.status != 302 && !errors.As(err, &protocol)) || calls != 1 || !body.closed {
			t.Fatalf("status=%d error=%v calls=%d", item.status, err, calls)
		}
	}
}

func TestPrometheusReadCausesAndBounds(t *testing.T) {
	for _, status := range []int{200, 421} {
		body := &prometheusBody{Reader: prometheusRead(func(p []byte) (int, error) { return copy(p, `{"error":{"code":"wrong_region"}}`), io.ErrUnexpectedEOF })}
		c := prometheusClient(t, &prometheusSource{}, func(*http.Request) (*http.Response, error) {
			return &http.Response{StatusCode: status, Header: http.Header{"Content-Type": {prometheusMedia}}, Body: body}, nil
		})
		_, err := c.ProjectPrometheusMetrics(t.Context(), prometheusProject)
		if !errors.Is(err, io.ErrUnexpectedEOF) || !body.closed {
			t.Fatal(err)
		}
		var api *APIError
		if status == 421 && (!errors.As(err, &api) || api.Code != ErrWrongRegion) {
			t.Fatal(err)
		}
	}
	for _, size := range []int{4, 5, 64<<10 + 1} {
		status := 200
		if size > 5 {
			status = 503
		}
		body := &prometheusBody{Reader: strings.NewReader(strings.Repeat("x", size))}
		c := prometheusClient(t, &prometheusSource{}, func(*http.Request) (*http.Response, error) {
			return &http.Response{StatusCode: status, Header: http.Header{"Content-Type": {prometheusMedia}}, Body: body}, nil
		})
		c.maxResponse = 4 // A smaller private bound; no public tuning option.
		out, err := c.ProjectPrometheusMetrics(t.Context(), prometheusProject)
		if size == 4 {
			if err != nil || len(out.Body) != 4 {
				t.Fatal(err)
			}
		} else if err == nil {
			t.Fatal("unbounded response")
		}
		var api *APIError
		if status == 503 && (!errors.As(err, &api) || len(api.Body) != 64<<10 || api.Unwrap() == nil) {
			t.Fatal(err)
		}
		if !body.closed {
			t.Fatal("body not closed")
		}
	}
}

func TestPrometheusCancellationAndAcquisitionDeadline(t *testing.T) {
	ctx, cancel := context.WithCancel(t.Context())
	sentinel := errors.New("token unavailable")
	source := &prometheusSource{fetch: func(ctx context.Context) (string, error) {
		deadline, ok := ctx.Deadline()
		if !ok || time.Until(deadline) > 30*time.Second {
			t.Error("acquisition not bounded")
		}
		cancel()
		return "", sentinel
	}}
	c := prometheusClient(t, source, func(*http.Request) (*http.Response, error) { t.Error("unexpected dispatch"); return nil, sentinel })
	if _, err := c.ProjectPrometheusMetrics(ctx, prometheusProject); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	if _, err := c.ProjectPrometheusMetrics(t.Context(), "42"); err == nil || source.calls != 1 {
		t.Fatal("invalid project reached auth")
	}
	if _, err := c.ProjectPrometheusMetrics(ctx, prometheusProject); !errors.Is(err, context.Canceled) || source.calls != 1 {
		t.Fatal(err)
	}
	ctx, cancel = context.WithCancel(t.Context())
	defer cancel()
	body := &prometheusBody{Reader: prometheusRead(func([]byte) (int, error) { cancel(); return 0, io.ErrUnexpectedEOF })}
	c = prometheusClient(t, &prometheusSource{}, func(*http.Request) (*http.Response, error) {
		return &http.Response{StatusCode: 200, Header: http.Header{"Content-Type": {prometheusMedia}}, Body: body}, nil
	})
	if _, err := c.ProjectPrometheusMetrics(ctx, prometheusProject); !errors.Is(err, context.Canceled) || !body.closed {
		t.Fatal(err)
	}
}

func TestPrometheusAcquisitionAndTransportCauses(t *testing.T) {
	sentinel := errors.New("unavailable")
	source := &prometheusSource{fetch: func(context.Context) (string, error) { return "", sentinel }}
	calls := 0
	c := prometheusClient(t, source, func(*http.Request) (*http.Response, error) { calls++; return nil, sentinel })
	if _, err := c.ProjectPrometheusMetrics(t.Context(), prometheusProject); !errors.Is(err, sentinel) || calls != 0 {
		t.Fatal(err)
	}
	source.fetch = nil
	if _, err := c.ProjectPrometheusMetrics(t.Context(), prometheusProject); !errors.Is(err, sentinel) || calls != 1 || source.invalidations != 0 {
		t.Fatal(err)
	}
	ctx, cancel := context.WithDeadline(t.Context(), time.Now().Add(-time.Second))
	defer cancel()
	if _, err := c.ProjectPrometheusMetrics(ctx, prometheusProject); !errors.Is(err, context.DeadlineExceeded) || calls != 1 {
		t.Fatal(err)
	}
}
