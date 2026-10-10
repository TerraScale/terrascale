package tracing

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

const (
	incomingTrace = "4bf92f3577b34da6a3ce929d0e0e4736"
	incoming      = "00-" + incomingTrace + "-00f067aa0ba902b7-01"
)

func TestMiddlewareContinuesValidTraceparent(t *testing.T) {
	t.Parallel()
	var seen string
	handler := Middleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		seen, _ = TraceID(r.Context())
	}))
	request := httptest.NewRequest(http.MethodGet, "/", nil)
	request.Header.Set("traceparent", incoming)
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	if seen != incomingTrace {
		t.Fatalf("trace ID = %q, want the incoming %q", seen, incomingTrace)
	}
	if got := response.Header().Get("traceparent"); !strings.HasPrefix(got, "00-"+incomingTrace+"-") {
		t.Fatalf("response traceparent = %q, want the incoming trace", got)
	}
}

func TestMiddlewareStartsTraceForInvalidTraceparent(t *testing.T) {
	t.Parallel()
	for _, header := range []string{
		"",
		"01-" + incomingTrace + "-00f067aa0ba902b7-01",
		"00-4BF92F3577B34DA6A3CE929D0E0E4736-00f067aa0ba902b7-01",
		"00-00000000000000000000000000000000-00f067aa0ba902b7-01",
		"00-4bf92f3577b34da6a3ce929d0e0e47-00f067aa0ba902b7-01",
		incoming + "-extra",
	} {
		var seen string
		handler := Middleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			seen, _ = TraceID(r.Context())
		}))
		request := httptest.NewRequest(http.MethodGet, "/", nil)
		request.Header.Set("traceparent", header)
		handler.ServeHTTP(httptest.NewRecorder(), request)
		if _, ok := parseTraceparent("00-" + seen + "-00f067aa0ba902b7-01"); !ok || seen == incomingTrace {
			t.Fatalf("header %q: trace ID %q, want a new valid one", header, seen)
		}
	}
}

func TestTransportSendsTraceOnlyInsideRequest(t *testing.T) {
	t.Parallel()
	received := make(chan string, 2)
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		received <- r.Header.Get("traceparent")
	}))
	defer upstream.Close()
	client := &http.Client{Transport: Transport(http.DefaultTransport)}

	traced, err := http.NewRequestWithContext(context.WithValue(context.Background(), contextKey{}, incomingTrace), http.MethodGet, upstream.URL, nil)
	if err != nil {
		t.Fatal(err)
	}
	response, err := client.Do(traced)
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if got := <-received; !strings.HasPrefix(got, "00-"+incomingTrace+"-") {
		t.Fatalf("outgoing traceparent = %q, want the request's trace", got)
	}
	if traced.Header.Get("traceparent") != "" {
		t.Fatal("transport modified the caller's request")
	}

	plain, err := http.NewRequest(http.MethodGet, upstream.URL, nil)
	if err != nil {
		t.Fatal(err)
	}
	response, err = client.Do(plain)
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if got := <-received; got != "" {
		t.Fatalf("outgoing traceparent without a request = %q, want none", got)
	}
}
