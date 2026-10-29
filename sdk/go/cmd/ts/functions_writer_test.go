package main

import (
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

type invocationWriter struct{ writes, failAt int }

func (w *invocationWriter) Write(p []byte) (int, error) {
	w.writes++
	if w.writes == w.failAt {
		return 0, io.ErrClosedPipe
	}
	return len(p), nil
}

func TestFunctionInvocationWriterFailures(t *testing.T) {
	t.Parallel()
	for _, status := range []int{200, 400} {
		for _, failAt := range []int{1, 2} {
			t.Run(fmt.Sprintf("%d-write%d", status, failAt), func(t *testing.T) {
				t.Parallel()
				server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
					w.WriteHeader(status)
					fmt.Fprint(w, `{"reason":"handler response"}`)
				}))
				t.Cleanup(server.Close)
				c, err := v1.New(v1.Config{BaseURL: server.URL, Token: "fixture.jwt.signature"})
				if err != nil {
					t.Fatal(err)
				}
				out := &invocationWriter{failAt: failAt}
				err = functions(t.Context(), c.Database("01M5N8CG630T6P449ES15GNZ8N", "app"), "functions invoke", functionFlags{}, []string{"hello"}, out)
				if !errors.Is(err, io.ErrClosedPipe) {
					t.Fatalf("writer failure=%v", err)
				}
				if status == 400 {
					var api *v1.APIError
					if !errors.As(err, &api) || api.Status != status {
						t.Fatalf("handler error lost: %v", err)
					}
				}
			})
		}
	}
}
