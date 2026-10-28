package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptrace"
	"strings"
	"sync/atomic"
	"time"

	"terrascale.tech/sdk/v1"
)

const (
	parityHost   = "api.terrascale.tech"
	parityOrigin = "https://" + parityHost
	// parityEvidence states what the attempt counts can and cannot show.
	parityEvidence = "RoundTripper attempts; net/http internal wire replays are not observable"
)

// parityTransport sees every attempt, including retries hidden by the SDK.
// Pin only public document reads; never forward the pin to auth or another host.
type parityTransport struct {
	base                                                    http.RoundTripper
	region, protocol                                        string
	attempts, bad, bodyErrors, connections, gotConn, h1, h2 atomic.Int64
	maxConnectionUS, maxHeadersUS                           atomic.Int64
}

// parityCounts are transport counters; RoundTrip and body reads update them.
type parityCounts struct {
	Attempts    int64 `json:"attempts"`
	Bad         int64 `json:"bad_attempts"` // wrong status, region or protocol, or a failed exchange
	BodyErrors  int64 `json:"body_errors"`  // a body read failed after a valid header
	Connections int64 `json:"connections"`
	GotConn     int64 `json:"got_conn"` // httptrace connection acquisitions; diagnostic only
	H1          int64 `json:"h1"`
	H2          int64 `json:"h2"`
}

func (t *parityTransport) counts() parityCounts {
	return parityCounts{t.attempts.Load(), t.bad.Load(), t.bodyErrors.Load(), t.connections.Load(), t.gotConn.Load(), t.h1.Load(), t.h2.Load()}
}

func (c parityCounts) minus(before parityCounts) parityCounts {
	return parityCounts{c.Attempts - before.Attempts, c.Bad - before.Bad, c.BodyErrors - before.BodyErrors,
		c.Connections - before.Connections, c.GotConn - before.GotConn, c.H1 - before.H1, c.H2 - before.H2}
}

func (t *parityTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	if request.URL.Scheme != "https" || request.URL.Host != parityHost || request.Method != http.MethodGet || !strings.HasSuffix(request.URL.Path, "/documents/by-key") {
		return nil, errors.New("parity transport accepts only public document reads")
	}
	started := time.Now()
	defer func() { parityMax(&t.maxHeadersUS, time.Since(started).Microseconds()) }()
	request = request.Clone(request.Context())
	request.Header.Set("X-TerraScale-Region-Pin", t.region)
	request = request.WithContext(httptrace.WithClientTrace(request.Context(), &httptrace.ClientTrace{GotConn: func(info httptrace.GotConnInfo) {
		parityMax(&t.maxConnectionUS, time.Since(started).Microseconds())
		t.gotConn.Add(1)
		if !info.Reused {
			t.connections.Add(1)
		}
	}}))
	t.attempts.Add(1)
	response, err := t.base.RoundTrip(request)
	if err != nil {
		t.bad.Add(1)
		return nil, err
	}
	if response.ProtoMajor == 1 {
		t.h1.Add(1)
	}
	if response.ProtoMajor == 2 {
		t.h2.Add(1)
	}
	if response.StatusCode != http.StatusOK || len(response.Header.Values("X-TerraBase-Region")) != 1 || response.Header.Get("X-TerraBase-Region") != t.region ||
		t.protocol == "raw-h1" && response.ProtoMajor != 1 || t.protocol == "raw-h2" && response.ProtoMajor != 2 {
		// Counted, then passed on unchanged so the SDK's shipped refresh and retry policy still applies.
		t.bad.Add(1)
	}
	response.Body = &parityBody{ReadCloser: response.Body, errors: &t.bodyErrors}
	return response, nil
}

// parityBody counts a failed body read, including after a valid 200 header:
// the SDK then retries a read without any visible non-200 attempt.
type parityBody struct {
	io.ReadCloser
	errors *atomic.Int64
	failed bool
}

func (b *parityBody) Read(p []byte) (int, error) {
	n, err := b.ReadCloser.Read(p)
	if err != nil && err != io.EOF && !b.failed {
		b.failed = true
		b.errors.Add(1)
	}
	return n, err
}

// Maxima include readiness and drain. They diagnose rejected cells; they are
// not latency samples from the recorded window.
func parityMax(counter *atomic.Int64, value int64) {
	for old := counter.Load(); value > old; old = counter.Load() {
		if counter.CompareAndSwap(old, value) {
			return
		}
	}
}

type parityDocument struct {
	id, version string
	body        json.RawMessage
}

func (d parityDocument) check(document *v1.Document) error {
	if document == nil || document.ID != d.id || document.Version != d.version || document.State != v1.StateValue || !bytes.Equal(document.Document, d.body) {
		return errors.New("parity document identity, version, state or body mismatch")
	}
	return nil
}

// parityTarget is one cell's read: the lane, the pinned region and the document.
type parityTarget struct {
	Lane, Region, Project, Database string
	Document                        parityDocument
}
