package v1

import (
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
)

func TestAPIErrorOwnsResponseHeaders(t *testing.T) {
	header := http.Header{"X-Route": {"first", "second"}}
	api := parseAPIError(http.StatusMisdirectedRequest, header, []byte(`{"error":{"code":"cursor_elsewhere"}}`))
	header["X-Route"][0] = "changed"
	header.Set("X-New", "later")
	if api.Header.Get("X-Route") != "first" || api.Header.Get("X-New") != "" {
		t.Fatalf("error shares mutable response headers: %v", api.Header)
	}
}

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
	_, err = c.Database("01ARZ3NDEKTSV4RRFFQ69G5FA8", "main").Collection("users").Get(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD2")
	var apiErr *APIError
	if !errors.As(err, &apiErr) || !errors.Is(err, io.ErrUnexpectedEOF) || apiErr.RequestID != "fixture-read" || string(apiErr.Body) != `{"error":` {
		t.Fatalf("error=%+v", err)
	}
}

func TestErrorReadLimitPreservesBoundedBody(t *testing.T) {
	c, err := New(Config{Token: "fixture.jwt.sig", HTTPClient: &http.Client{Transport: errorReaderTransport{io.NopCloser(strings.NewReader("too long"))}}})
	if err != nil {
		t.Fatal(err)
	}
	c.maxResponse = 4
	_, err = c.Database("01ARZ3NDEKTSV4RRFFQ69G5FA8", "main").Collection("users").Get(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD2")
	var apiErr *APIError
	if !errors.As(err, &apiErr) || apiErr.RequestID != "fixture-read" || string(apiErr.Body) != "too " || apiErr.Unwrap() == nil {
		t.Fatalf("error=%+v", err)
	}
}
