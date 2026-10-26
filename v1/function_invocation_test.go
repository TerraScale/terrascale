package v1_test

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"reflect"
	"sync/atomic"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestFunctionInvocationResponseAndQuery(t *testing.T) {
	for _, status := range []int{200, 201, 204, 401, 403, 409, 422, 429, 503} {
		t.Run(http.StatusText(status), func(t *testing.T) {
			query := url.Values{"q": {"a&b /+", "second"}, "empty": {""}}
			var calls atomic.Int32
			body := "owned handler response"
			if status == http.StatusNoContent {
				body = ""
			}
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls.Add(1)
				if r.Method != http.MethodPost || r.URL.Path != parityDatabase+"/functions/hello/invoke" || !reflect.DeepEqual(r.URL.Query(), query) {
					t.Error("invocation method, route or escaped query changed")
				}
				w.Header().Set("Content-Type", "text/plain; charset=utf-8")
				w.Header().Set("Retry-After", "0")
				w.WriteHeader(status)
				_, _ = w.Write([]byte(body))
			}))
			defer server.Close()
			source := &invalidatingSource{}
			client, err := tb.New(tb.Config{BaseURL: server.URL, TokenSource: source})
			if err != nil {
				t.Fatal(err)
			}
			response, err := client.Database(parityProject, "main").InvokeFunction(t.Context(), "hello", nil, query)
			if status < 300 {
				if err != nil || response == nil || response.StatusCode != status || response.ContentType != "text/plain; charset=utf-8" || string(response.Body) != body {
					t.Fatal("successful handler status, media or bytes lost")
				}
			} else {
				var api *tb.APIError
				if response != nil || !errors.As(err, &api) || api.Status != status || api.Header.Get("Content-Type") != "text/plain; charset=utf-8" || string(api.Body) != body {
					t.Fatal("handler error status, media or bytes lost")
				}
			}
			if calls.Load() != 1 || source.tokens.Load() != 1 || source.invalidations.Load() != 0 {
				t.Fatal("handler response triggered a retry or credential refresh")
			}
			if !reflect.DeepEqual(query, url.Values{"q": {"a&b /+", "second"}, "empty": {""}}) {
				t.Fatal("invocation mutated caller query values")
			}
		})
	}
}

type functionCanceledSource struct{ calls atomic.Int32 }

func (s *functionCanceledSource) Token(ctx context.Context) (string, error) {
	s.calls.Add(1)
	<-ctx.Done()
	return "", ctx.Err()
}

func TestFunctionInvocationCancellationAndInputBound(t *testing.T) {
	source := &functionCanceledSource{}
	client, err := tb.New(tb.Config{TokenSource: source})
	if err != nil {
		t.Fatal(err)
	}
	database := client.Database(parityProject, "main")
	if _, err := database.InvokeFunction(t.Context(), "hello", make([]byte, (1<<20)+1), nil); err == nil || source.calls.Load() != 0 {
		t.Fatal("oversized input reached credential acquisition")
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := database.InvokeFunction(ctx, "hello", nil, nil); !errors.Is(err, context.Canceled) {
		t.Fatal("caller cancellation lost during credential acquisition")
	}
}
