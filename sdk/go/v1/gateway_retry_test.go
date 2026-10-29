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

func TestUntypedServiceUnavailableIsNotRetried(t *testing.T) {
	if _, retry := retryDelay(&APIError{Status: http.StatusServiceUnavailable, Code: "http_503"}, 0, true, true); retry {
		t.Fatal("untyped 503 retried")
	}
}

func TestAPIErrorTextNamesTheRequest(t *testing.T) {
	e := parseAPIError(http.StatusNotFound, http.Header{"X-Request-Id": {"01M5ZQNT651DKH8SQV6N4Y11V0"}}, []byte(`{"error":{"code":"unknown_collection","message":"the collection does not exist"}}`))
	if got, want := e.Error(), "terrabase: 404 unknown_collection: the collection does not exist [request 01M5ZQNT651DKH8SQV6N4Y11V0]"; got != want {
		t.Fatalf("%q, want %q", got, want)
	}
	if got := parseAPIError(http.StatusNotFound, http.Header{}, nil).Error(); got != "terrabase: 404 http_404" {
		t.Fatal(got)
	}
}

func TestTenantBusyAdmissionRefusalIsRetriedAfterRetryAfter(t *testing.T) {
	calls := 0
	c, err := New(Config{Token: "fixture.jwt.sig", HTTPClient: &http.Client{Transport: boundedTransport(func(r *http.Request) (*http.Response, error) {
		calls++
		if calls == 1 {
			return &http.Response{StatusCode: http.StatusTooManyRequests, Header: http.Header{"Retry-After": {"0"}},
				Body: io.NopCloser(strings.NewReader(`{"error":{"code":"tenant_busy","message":"organization request budget exceeded"}}`))}, nil
		}
		return &http.Response{StatusCode: http.StatusOK, Body: io.NopCloser(strings.NewReader(`{}`))}, nil
	})}})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := c.do(t.Context(), request{method: http.MethodGet, replaySafe: true}); err != nil || calls != 2 {
		t.Fatalf("error=%v calls=%d", err, calls)
	}
	if _, retry := retryDelay(&APIError{Status: http.StatusTooManyRequests, Code: ErrTenantBusy}, 0, false, false); retry {
		t.Fatal("unsafe request retried")
	}
}
