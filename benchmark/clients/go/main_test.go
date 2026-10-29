package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"

	"terrascale.tech/sdk/v1"
	"terrascale.tech/ts-benchmark/internal/apibench"
)

func TestPayloadBytesExact(t *testing.T) {
	t.Parallel()
	for _, size := range []int{256, 4096, 65536} {
		bodies, err := payloads(size)
		if err != nil {
			t.Fatal(err)
		}
		for g, body := range bodies {
			var d docBody
			if len(body) != size || json.Unmarshal(body, &d) != nil || d.G != g {
				t.Fatalf("size %d g %d: %d bytes, decoded %+v", size, g, len(body), d)
			}
		}
	}
}

func TestSeedSize(t *testing.T) {
	t.Parallel()
	if err := (&bench{}).ensure(context.Background(), 0); err != nil {
		t.Fatalf("auth.token size 0 needs no seed: %v", err)
	}
	for _, c := range []struct {
		s    apibench.Step
		want int
	}{
		{apibench.Step{Scenario: "auth.token"}, 0},
		{apibench.Step{Scenario: "doc.get", PayloadBytes: 4096}, 4096},
		{apibench.Step{Scenario: "query.small", PayloadBytes: 4096}, 256},
	} {
		if got := seedSize(c.s); got != c.want {
			t.Errorf("seedSize(%s, %d) = %d, want %d", c.s.Scenario, c.s.PayloadBytes, got, c.want)
		}
	}
}

func TestStatusKeys(t *testing.T) {
	t.Parallel()
	for _, c := range []struct {
		err  error
		want string
	}{
		{nil, "ok"},
		{&v1.APIError{Status: 429}, "429"},
		{fmt.Errorf("op: %w", &v1.APIError{Status: 503}), "503"},
		{context.DeadlineExceeded, "timeout"},
		{os.ErrDeadlineExceeded, "timeout"},
		{context.Canceled, "canceled"},
		{errors.New("connection reset"), "transport"},
	} {
		if got := status(c.err); got != c.want {
			t.Errorf("status(%v) = %q, want %q", c.err, got, c.want)
		}
	}
}

func TestCredentialsNeedOwnerOnly(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "credentials.json")
	if err := os.WriteFile(path, []byte(`{"api_key":"01ARZ3NDEKTSV4RRFFQ69G5FAV_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","project":"p1"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(path, 0o644); err != nil {
		t.Fatal(err)
	}
	if _, _, err := loadCredentials(path); err == nil {
		t.Fatal("group-readable credentials accepted")
	}
	if err := os.Chmod(path, 0o600); err != nil {
		t.Fatal(err)
	}
	key, project, err := loadCredentials(path)
	if err != nil || key != "01ARZ3NDEKTSV4RRFFQ69G5FAV_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" || project != "p1" {
		t.Fatalf("loadCredentials = %q %q %v", key, project, err)
	}
}

func TestStartupLine(t *testing.T) {
	t.Parallel()
	var out bytes.Buffer
	if err := startup(&out); err != nil {
		t.Fatal(err)
	}
	var st apibench.Startup
	if err := json.Unmarshal(out.Bytes(), &st); err != nil || st.Type != "startup" || st.InitMS <= 0 {
		t.Fatalf("startup line %q: %v", out.String(), err)
	}
}

func TestRawDoStatusKeys(t *testing.T) {
	t.Parallel()
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.Header.Get("Authorization") != "Bearer tok":
			w.WriteHeader(http.StatusUnauthorized)
		case r.URL.Path == "/missing":
			w.WriteHeader(http.StatusNotFound)
		default:
			_, _ = w.Write([]byte(`{}`))
		}
	}))
	defer srv.Close()
	ctx := context.Background()
	for target, want := range map[string]string{"/doc": "ok", "/missing": "404"} {
		if got := rawDo(ctx, srv.Client(), http.MethodGet, srv.URL+target, nil, "tok"); got != want {
			t.Errorf("rawDo %s = %q, want %q", target, got, want)
		}
	}
	if got := rawDo(ctx, srv.Client(), http.MethodPost, srv.URL+"/doc", []byte(`{}`), "wrong"); got != "401" {
		t.Errorf("bad token = %q, want 401", got)
	}
}
