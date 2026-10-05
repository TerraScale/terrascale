package terrabasetest

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"slices"
	"strconv"
	"strings"
	"testing"
	"time"
)

type failedDownload struct{}

func (failedDownload) RoundTrip(*http.Request) (*http.Response, error) {
	return nil, errors.New("injected download failure")
}

func TestStartPrerequisitePolicy(t *testing.T) {
	if mode := os.Getenv("TERRABASE_POLICY_HELPER"); mode != "" {
		if mode == "download" {
			http.DefaultClient = &http.Client{Transport: failedDownload{}}
		}
		Start(t)
		t.Fatal("unexpected server start")
	}
	if runtime.GOOS != "linux" || runtime.GOARCH != "amd64" {
		t.Skip("cache and download error checks need the supported release platform")
	}
	exe, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct {
		name, mode, require, message string
		skip                         bool
	}{
		{"missing optional", "missing", "", "prerequisite unavailable", true},
		{"missing required", "missing", "1", "prerequisite unavailable", false},
		{"corrupt cache", "corrupt", "", "pinned SHA-256", false},
		{"unreadable cache", "directory", "", "read TerraBase cache", false},
		{"failed authenticated download", "download", "", "injected download failure", false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			dir := t.TempDir()
			path := filepath.Join(dir, "terrabase-"+Commit)
			switch tc.mode {
			case "corrupt":
				if err := os.WriteFile(path, []byte("corrupt artifact"), 0o600); err != nil {
					t.Fatal(err)
				}
			case "directory":
				if err := os.Mkdir(path, 0o700); err != nil {
					t.Fatal(err)
				}
			}
			ctx, cancel := context.WithTimeout(t.Context(), 15*time.Second)
			defer cancel()
			// A fresh test process also gives Binary's sync.Once a fresh state.
			cmd := exec.CommandContext(ctx, exe, "-test.run=^TestStartPrerequisitePolicy$", "-test.v")
			cmd.Env = slices.DeleteFunc(os.Environ(), func(e string) bool { return strings.HasPrefix(e, "TERRABASE_") })
			cmd.Env = append(cmd.Env, "TERRABASE_CACHE="+dir, "TERRABASE_REQUIRE="+tc.require, "TERRABASE_POLICY_HELPER="+tc.mode)
			if tc.mode == "download" {
				cmd.Env = append(cmd.Env, "TERRABASE_TOKEN=test-download-token")
			}
			out, err := cmd.CombinedOutput()
			if (err == nil) != tc.skip || strings.Contains(string(out), "--- SKIP:") != tc.skip || !strings.Contains(string(out), tc.message) {
				t.Fatalf("want skip=%t and %q, got %v:\n%s", tc.skip, tc.message, err, out)
			}
		})
	}
}

func TestServerAssignedPorts(t *testing.T) {
	a, b := Start(t), Start(t)
	ports := map[string]bool{}
	for _, raw := range []string{a.URL, a.AdminURL, b.URL, b.AdminURL} {
		u, err := url.Parse(raw)
		if err != nil || u.Port() == "" || ports[u.Port()] {
			t.Fatalf("missing or shared listener: %s", raw)
		}
		ports[u.Port()] = true
	}
}

// stallTransport injects a header or body stall in front of the real server.
type stallTransport struct {
	base http.RoundTripper
	body bool
}

func (s stallTransport) RoundTrip(req *http.Request) (*http.Response, error) {
	deadline, ok := req.Context().Deadline()
	if !ok || time.Until(deadline) > 10*time.Second {
		return nil, errors.New("admin request has no bounded deadline")
	}
	res, err := s.base.RoundTrip(req)
	if err != nil {
		return nil, err
	}
	if s.body {
		res.Body = stalledBody{ReadCloser: res.Body, ctx: req.Context()}
		return res, nil
	}
	defer res.Body.Close()
	<-req.Context().Done()
	return nil, req.Context().Err()
}

type stalledBody struct {
	io.ReadCloser
	ctx context.Context
}

func (b stalledBody) Read([]byte) (int, error) {
	<-b.ctx.Done()
	return 0, b.ctx.Err()
}

func TestAdminCallDeadline(t *testing.T) {
	s := Start(t)
	for _, body := range []bool{false, true} {
		for _, method := range []string{http.MethodGet, http.MethodPost} {
			t.Run(method+"/body="+strconv.FormatBool(body), func(t *testing.T) {
				copy := *s
				copy.Client = &http.Client{Transport: stallTransport{base: s.Client.Transport, body: body}}
				ctx, cancel := context.WithTimeout(t.Context(), 200*time.Millisecond)
				defer cancel()
				path, payload := "/healthz", []byte(nil)
				if method == http.MethodPost {
					path, payload = "/admin/api-keys", []byte(`{"name":"deadline","scopes":["documents:read"],"organizationId":"test"}`)
				}
				_, _, err := copy.adminCall(ctx, method, path, payload)
				if !errors.Is(err, context.DeadlineExceeded) {
					t.Fatalf("stalled admin request: %v", err)
				}
			})
		}
	}
}
