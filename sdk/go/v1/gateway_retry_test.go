package v1

import (
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
)

func TestReadsRetryGatewayFailuresAndMutationsDoNot(t *testing.T) {
	for _, status := range []int{http.StatusBadGateway, http.StatusGatewayTimeout} {
		for _, req := range []request{
			{method: http.MethodGet, replaySafe: true},
			{method: http.MethodPost, replaySafe: true, readOnly: true},
			{method: http.MethodPut, replaySafe: true, body: []byte(`{}`)}, // an If-Match write
			{method: http.MethodPost, body: []byte(`{}`)},
		} {
			calls := 0
			c, err := New(Config{Token: "fixture.jwt.sig", HTTPClient: &http.Client{Transport: boundedTransport(func(r *http.Request) (*http.Response, error) {
				calls++
				if calls == 1 {
					return &http.Response{StatusCode: status, Header: http.Header{"Content-Type": {"text/html"}}, Body: io.NopCloser(strings.NewReader("<html>gateway</html>"))}, nil
				}
				return &http.Response{StatusCode: http.StatusOK, Body: io.NopCloser(strings.NewReader(`{}`))}, nil
			})}})
			if err != nil {
				t.Fatal(err)
			}
			_, err = c.do(t.Context(), req)
			read := req.method == http.MethodGet || req.readOnly
			var api *APIError
			if read && (err != nil || calls != 2) || !read && (!errors.As(err, &api) || api.Status != status || calls != 1) {
				t.Fatalf("%d %s readOnly=%v: error=%v calls=%d", status, req.method, req.readOnly, err, calls)
			}
		}
	}
}
