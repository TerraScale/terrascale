package main

import (
	"bytes"
	"context"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

type exportTransport func(*http.Request) (*http.Response, error)

func (f exportTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

const exportProject = "01ARZ3NDEKTSV4RRFFQ69G5FAV"

func exportClient(t *testing.T, dump string) *v1.Client {
	t.Helper()
	hc := &http.Client{Transport: exportTransport(func(r *http.Request) (*http.Response, error) {
		var body string
		switch {
		case r.URL.Path == "/terrascale/oauth/token":
			body = `{"access_token":"header.` + exportProject + `.signature","token_type":"Bearer","expires_in":3600}`
		case r.Method == "GET" && strings.HasSuffix(r.URL.Path, "/databases/app/export"):
			body = dump
		default:
			t.Errorf("unexpected request %s %s", r.Method, r.URL)
			body = "{}"
		}
		return &http.Response{StatusCode: 200, Header: http.Header{"Content-Type": {"text/plain"}},
			Body: io.NopCloser(strings.NewReader(body)), Request: r}, nil
	})}
	client, err := v1.New(v1.Config{APIKey: exportProject + "_" + strings.Repeat("a", 52), HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	return client
}

func TestDbExport(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		name    string
		dump    string
		wantErr bool
	}{
		{name: "complete", dump: "BEGIN;\nCREATE TABLE t(id);\nCOMMIT;\n"},
		{name: "truncated", dump: "BEGIN;\nCREATE TABLE t(id);\n", wantErr: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			s := &session{tb: exportClient(t, tc.dump), project: exportProject}
			target := filepath.Join(t.TempDir(), "dump.sql")
			err := dbExport(context.Background(), s, "app", target, false, &bytes.Buffer{})
			if tc.wantErr {
				if err == nil {
					t.Fatal("expected an error")
				}
				for _, path := range []string{target, target + ".partial"} {
					if _, statErr := os.Stat(path); !os.IsNotExist(statErr) {
						t.Fatalf("%s must not exist", path)
					}
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			data, err := os.ReadFile(target)
			if err != nil {
				t.Fatal(err)
			}
			if string(data) != tc.dump {
				t.Fatalf("dump mismatch: %q", data)
			}
			if _, err := os.Stat(target + ".partial"); !os.IsNotExist(err) {
				t.Fatal("partial file left behind")
			}
		})
	}
}
