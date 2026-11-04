package v1

import (
	"io"
	"net/http"

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
