package v1

import (
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
)

type brokenErrorReader struct{ read bool }

func (r *brokenErrorReader) Read(p []byte) (int, error) {
	if r.read {
		return 0, io.ErrUnexpectedEOF
	}
	r.read = true
	return copy(p, `{"error":`), io.ErrUnexpectedEOF
}
func (*brokenErrorReader) Close() error { return nil }

type errorReaderTransport struct{ body io.ReadCloser }

func (tr errorReaderTransport) RoundTrip(*http.Request) (*http.Response, error) {
	return &http.Response{StatusCode: 403, Header: http.Header{"X-Request-Id": {"fixture-read"}}, Body: tr.body}, nil
}

func TestErrorReadFailurePreservesPartialBody(t *testing.T) {
	c, err := New(Config{Token: "fixture.jwt.sig", HTTPClient: &http.Client{Transport: errorReaderTransport{&brokenErrorReader{}}}})
	if err != nil {
		t.Fatal(err)
	}
	_, err = c.Database("01ARZ3NDEKTSV4RRFFQ69G5FA8", "main").Collection("users").Get(t.Context(), "key")
	var apiErr *APIError
	if !errors.As(err, &apiErr) || !errors.Is(err, io.ErrUnexpectedEOF) || apiErr.RequestID != "fixture-read" || string(apiErr.Body) != `{"error":` {
		t.Fatalf("error=%+v", err)
	}
}

func TestErrorReadLimitPreservesBoundedBody(t *testing.T) {
	c, err := New(Config{Token: "fixture.jwt.sig", MaxResponseBytes: 4, HTTPClient: &http.Client{Transport: errorReaderTransport{io.NopCloser(strings.NewReader("too long"))}}})
	if err != nil {
		t.Fatal(err)
	}
	_, err = c.Database("01ARZ3NDEKTSV4RRFFQ69G5FA8", "main").Collection("users").Get(t.Context(), "key")
	var apiErr *APIError
	if !errors.As(err, &apiErr) || apiErr.RequestID != "fixture-read" || string(apiErr.Body) != "too " || apiErr.Unwrap() == nil {
		t.Fatalf("error=%+v", err)
	}
}
